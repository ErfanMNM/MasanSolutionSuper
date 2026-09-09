using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;

public enum eRDCZPState
{
    Connected,
    Disconnected,
    Received,
    Reconnecting
}

public sealed class RDCZP
{
    private readonly object _sync = new();
    private readonly TimeSpan _reconnectDelay;
    private Socket? _client;
    private CancellationTokenSource? _runCancellation;
    private bool _isRunning;
    private bool _connected;

    public string IP { get; set; }
    public int Port { get; set; }
    public bool Connected => Volatile.Read(ref _connected);

    public delegate void ClientEventHandler(eRDCZPState state, string data);
    public event ClientEventHandler? ClientCallback;

    public RDCZP(string ip, int port, TimeSpan? reconnectDelay = null)
    {
        IP = ip;
        Port = port;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(3);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(IP, out var address) || Port is <= 0 or > 65535)
        {
            OnClientCallback(eRDCZPState.Disconnected, "Invalid IP address or port");
            return;
        }

        CancellationTokenSource runCancellation;
        lock (_sync)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("RDCZP is already running.");
            }

            _isRunning = true;
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = runCancellation;
        }

        try
        {
            await RunConnectionLoopAsync(new IPEndPoint(address, Port), runCancellation.Token);
        }
        finally
        {
            CleanupSocket();
            lock (_sync)
            {
                _runCancellation = null;
                _isRunning = false;
            }
            runCancellation.Dispose();
        }
    }

    public void Disconnect()
    {
        CancellationTokenSource? cancellation;
        Socket? socket;
        lock (_sync)
        {
            cancellation = _runCancellation;
            socket = _client;
        }

        cancellation?.Cancel();
        socket?.Dispose();
    }

    public async Task<bool> SendAsync(string data, CancellationToken cancellationToken = default)
    {
        Socket? socket;
        lock (_sync)
        {
            socket = _client;
        }

        if (string.IsNullOrEmpty(data) || socket == null || !Connected)
        {
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            var sent = 0;
            while (sent < bytes.Length)
            {
                var bytesSent = await socket.SendAsync(
                    bytes.AsMemory(sent), SocketFlags.None, cancellationToken);
                if (bytesSent == 0)
                {
                    return false;
                }

                sent += bytesSent;
            }
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            return false;
        }
    }

    private async Task RunConnectionLoopAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            OnClientCallback(eRDCZPState.Reconnecting, "Attempting to connect...");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            SetSocket(socket);
            var reason = "Connection closed by remote host.";

            try
            {
                await socket.ConnectAsync(endPoint, cancellationToken);
                Volatile.Write(ref _connected, true);
                OnClientCallback(eRDCZPState.Connected, "Connected successfully");
                reason = await ReceiveFramesAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                reason = $"Connection error: {exception.Message}";
            }
            finally
            {
                Volatile.Write(ref _connected, false);
                CleanupSocket(socket);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                OnClientCallback(eRDCZPState.Disconnected, reason);
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }

    private async Task<string> ReceiveFramesAsync(Socket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var frame = new StringBuilder();

        while (true)
        {
            var received = await socket.ReceiveAsync(bytes, SocketFlags.None, cancellationToken);
            if (received == 0)
            {
                return "Connection closed by remote host.";
            }

            var charCount = decoder.GetChars(bytes.AsSpan(0, received), chars, false);
            for (var index = 0; index < charCount; index++)
            {
                if (chars[index] is '\r' or '\n')
                {
                    if (frame.Length > 0)
                    {
                        OnClientCallback(eRDCZPState.Received, frame.ToString());
                        frame.Clear();
                    }
                }
                else
                {
                    frame.Append(chars[index]);
                }
            }
        }
    }

    private void SetSocket(Socket socket)
    {
        lock (_sync)
        {
            _client = socket;
        }
    }

    private void CleanupSocket(Socket? expectedSocket = null)
    {
        Socket? socket;
        lock (_sync)
        {
            if (expectedSocket != null && !ReferenceEquals(_client, expectedSocket)) return;
            socket = _client;
            _client = null;
        }
        socket?.Dispose();
    }

    private void OnClientCallback(eRDCZPState state, string data) => ClientCallback?.Invoke(state, data);
}
