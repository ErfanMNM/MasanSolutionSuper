using ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MasanCZCodeBackend;

/// <summary>
/// BackgroundService xử lý dữ liệu Datamatrix nhận được từ camera RD trên
/// một luồng riêng duy nhất (dedicated thread).
///
/// Ngữ nghĩa "drop-if-busy":
///   - Nếu worker đang rảnh, mã nhận được sẽ được enqueue và xử lý.
///   - Nếu worker đang bận (đang trong ProcessData) hoặc đã có mã chờ,
///     các mã tiếp theo sẽ bị bỏ qua (không xếp hàng đợi).
/// </summary>
internal sealed class ReceivedDataProcessor : BackgroundService
{
    private readonly RDCZP _camera;
    private readonly ILogger<ReceivedDataProcessor> _logger;

    // Khoá bảo vệ các trường trạng thái bên dưới
    private readonly object _sync = new();

    // Tín hiệu đánh thức worker thread khi có dữ liệu mới
    private readonly ManualResetEventSlim _signal = new(false);

    // Mã Datamatrix đang chờ worker xử lý
    private string? _pendingData;
    private bool _hasPending;

    // Worker có đang trong ProcessData() hay không
    private bool _isProcessing;

    // Cờ yêu cầu dừng worker (volatile: ghi từ ExecuteAsync, đọc từ worker thread)
    private volatile bool _stopRequested;

    public ReceivedDataProcessor(RDCZP camera, ILogger<ReceivedDataProcessor> logger)
    {
        _camera = camera;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _camera.ClientCallback += OnCameraEvent;

        // Khởi tạo luồng riêng duy nhất để xử lý Datamatrix
        var workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RD-ReceivedDataProcessor",
        };
        workerThread.Start();

        _logger.LogInformation("ReceivedDataProcessor đã khởi động luồng xử lý riêng.");

        try
        {
            // Vòng lặp kết nối của camera chạy vô tận cho đến khi host shutdown
            await _camera.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown bình thường
        }
        finally
        {
            _camera.ClientCallback -= OnCameraEvent;

            // Yêu cầu worker dừng
            _stopRequested = true;
            _signal.Set();
            workerThread.Join();

            _signal.Dispose();
            _logger.LogInformation("ReceivedDataProcessor đã dừng.");
        }
    }

    private void OnCameraEvent(eRDCZPState state, string data)
    {
        switch (state)
        {
            case eRDCZPState.Connected:
                _logger.LogInformation("Camera RD đã kết nối: {Message}", data);
                return;

            case eRDCZPState.Disconnected:
                _logger.LogWarning("Camera RD mất kết nối: {Message}", data);
                return;

            case eRDCZPState.Reconnecting:
                _logger.LogInformation("Camera RD đang kết nối lại: {Message}", data);
                return;

            case eRDCZPState.Received:
                HandleReceived(data);
                return;
        }
    }

    /// <summary>
    /// Đẩy mã nhận được vào worker với ngữ nghĩa "drop-if-busy".
    /// Được gọi từ thread của camera callback (có thể là thread bất kỳ).
    /// </summary>
    private void HandleReceived(string data)
    {
        lock (_sync)
        {
            if (_isProcessing || _hasPending)
            {
                // Worker đang bận HOẶc đã có mã chờ → bỏ qua mã mới
                _logger.LogDebug("Worker bận hoặc đã có mã chờ, bỏ qua: {Data}", data);
                return;
            }

            _pendingData = data;
            _hasPending = true;
        }

        // Đánh thức worker thread (Set có thể gọi từ bất kỳ thread nào)
        _signal.Set();
    }

    private void WorkerLoop()
    {
        var threadId = Thread.CurrentThread.ManagedThreadId;
        _logger.LogInformation("Worker thread {ThreadId} đã sẵn sàng.", threadId);

        while (!_stopRequested)
        {
            // Chờ tín hiệu có dữ liệu mới
            try
            {
                _signal.Wait();
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (_stopRequested) break;

            // Reset tín hiệu trước khi xử lý.
            // Lưu ý race: nếu event mới đến ngay lúc này, nó sẽ overwrite
            // _pendingData. Điều này được chấp nhận vì worker vẫn chưa bắt
            // đầu xử lý mã cũ — tức là "drop" mã cũ để lấy mã mới nhất.
            _signal.Reset();

            // Lấy dữ liệu và đánh dấu bận (atomic trong lock)
            string? data;
            lock (_sync)
            {
                if (!_hasPending) continue;
                data = _pendingData;
                _pendingData = null;
                _hasPending = false;
                _isProcessing = true;
            }

            try
            {
                _logger.LogInformation(
                    "[Worker {ThreadId}] Bắt đầu xử lý mã Datamatrix: {Data}",
                    threadId, data);
                ProcessData(data!);
                _logger.LogInformation(
                    "[Worker {ThreadId}] Đã xử lý xong mã Datamatrix: {Data}",
                    threadId, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Worker {ThreadId}] Lỗi khi xử lý mã Datamatrix: {Data}",
                    threadId, data);
            }
            finally
            {
                lock (_sync)
                {
                    _isProcessing = false;
                }
            }
        }

        _logger.LogInformation("Worker thread {ThreadId} đã thoát.", threadId);
    }

    /// <summary>
    /// Thay thế hàm này bằng nghiệp vụ thực tế (HTTP, DB, queue...).
    /// </summary>
    private void ProcessData(string data)
    {
        // TODO: thay bằng nghiệp vụ thực tế
        Thread.Sleep(500); // mô phỏng xử lý nặng 500ms để quan sát drop-if-busy
    }
}
