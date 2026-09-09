using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;
using Xunit;
using CameraClient = ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol.RDCZP;

namespace RDCZP.Tests;

public sealed class RDCZPTests
{
    [Fact]
    public async Task RunAsync_EmitsFramesForCrLfAndCrLfPair()
    {
        using var listener = StartListener(out var port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateUnbounded<(eRDCZPState State, string Data)>();
        var client = CreateClient(port, events);
        var runTask = client.RunAsync(cancellation.Token);

        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);

        await server.SendAsync(Encoding.UTF8.GetBytes("ONE\rTWO\nTHREE\r\n\r\n"), cancellation.Token);

        Assert.Equal("ONE", await WaitForFrameAsync(events.Reader, cancellation.Token));
        Assert.Equal("TWO", await WaitForFrameAsync(events.Reader, cancellation.Token));
        Assert.Equal("THREE", await WaitForFrameAsync(events.Reader, cancellation.Token));

        cancellation.Cancel();
        await AssertCanceledOrCompletedAsync(runTask);
    }

    [Fact]
    public async Task RunAsync_PreservesSplitFramesAndUtf8Characters()
    {
        using var listener = StartListener(out var port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateUnbounded<(eRDCZPState State, string Data)>();
        var client = CreateClient(port, events);
        var runTask = client.RunAsync(cancellation.Token);

        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);

        var payload = Encoding.UTF8.GetBytes("MÃ SẢN\n");
        await server.SendAsync(payload.AsMemory(0, 2), cancellation.Token);
        await Task.Delay(20, cancellation.Token);
        await server.SendAsync(payload.AsMemory(2, 3), cancellation.Token);
        await Task.Delay(20, cancellation.Token);
        await server.SendAsync(payload.AsMemory(5), cancellation.Token);

        Assert.Equal("MÃ SẢN", await WaitForFrameAsync(events.Reader, cancellation.Token));

        cancellation.Cancel();
        await AssertCanceledOrCompletedAsync(runTask);
    }

    [Fact]
    public async Task RunAsync_ReconnectsAfterRemoteDisconnect()
    {
        using var listener = StartListener(out var port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateUnbounded<(eRDCZPState State, string Data)>();
        var client = CreateClient(port, events);
        var runTask = client.RunAsync(cancellation.Token);

        using (var firstServer = await listener.AcceptSocketAsync(cancellation.Token))
        {
            await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);
        }

        await WaitForStateAsync(events.Reader, eRDCZPState.Disconnected, cancellation.Token);
        using var secondServer = await listener.AcceptSocketAsync(cancellation.Token);
        await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);
        await secondServer.SendAsync(Encoding.UTF8.GetBytes("AFTER-RECONNECT\r"), cancellation.Token);

        Assert.Equal("AFTER-RECONNECT", await WaitForFrameAsync(events.Reader, cancellation.Token));

        cancellation.Cancel();
        await AssertCanceledOrCompletedAsync(runTask);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsWithoutAnotherReconnect()
    {
        using var listener = StartListener(out var port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateUnbounded<(eRDCZPState State, string Data)>();
        var client = CreateClient(port, events);
        var runTask = client.RunAsync(cancellation.Token);

        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);

        cancellation.Cancel();
        await AssertCanceledOrCompletedAsync(runTask);
        await Task.Delay(100);

        var statesAfterStop = new List<eRDCZPState>();
        while (events.Reader.TryRead(out var item)) statesAfterStop.Add(item.State);
        Assert.DoesNotContain(eRDCZPState.Reconnecting, statesAfterStop);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task SendAsync_SendsCompleteUtf8Payload()
    {
        using var listener = StartListener(out var port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateUnbounded<(eRDCZPState State, string Data)>();
        var client = CreateClient(port, events);
        var runTask = client.RunAsync(cancellation.Token);

        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await WaitForStateAsync(events.Reader, eRDCZPState.Connected, cancellation.Token);
        var payload = string.Concat(Enumerable.Repeat("DỮ-LIỆU-", 16_384));
        var expected = Encoding.UTF8.GetBytes(payload);
        var received = new byte[expected.Length];

        var receiveTask = ReceiveExactlyAsync(server, received, cancellation.Token);
        Assert.True(await client.SendAsync(payload, cancellation.Token));
        await receiveTask;
        Assert.Equal(expected, received);

        cancellation.Cancel();
        await AssertCanceledOrCompletedAsync(runTask);
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static CameraClient CreateClient(
        int port,
        Channel<(eRDCZPState State, string Data)> events)
    {
        var client = new CameraClient("127.0.0.1", port, TimeSpan.FromMilliseconds(20));
        client.ClientCallback += (state, data) => events.Writer.TryWrite((state, data));
        return client;
    }

    private static async Task WaitForStateAsync(
        ChannelReader<(eRDCZPState State, string Data)> reader,
        eRDCZPState expected,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                if (item.State == expected) return;
            }
        }
    }

    private static async Task<string> WaitForFrameAsync(
        ChannelReader<(eRDCZPState State, string Data)> reader,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                if (item.State == eRDCZPState.Received) return item.Data;
            }
        }

        throw new InvalidOperationException("The event channel completed before a frame was received.");
    }

    private static async Task ReceiveExactlyAsync(
        Socket socket,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var received = 0;
        while (received < destination.Length)
        {
            var count = await socket.ReceiveAsync(destination.AsMemory(received), cancellationToken);
            if (count == 0) throw new EndOfStreamException("Socket closed before the payload was complete.");
            received += count;
        }
    }

    private static async Task AssertCanceledOrCompletedAsync(Task runTask)
    {
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
