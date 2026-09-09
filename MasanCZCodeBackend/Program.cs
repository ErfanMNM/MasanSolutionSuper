using ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MasanCZCodeBackend;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(new RDCZP("127.0.0.1", 9000));
        builder.Services.AddHostedService<CameraRDWorker>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}

internal sealed class CameraRDWorker : BackgroundService
{
    private readonly RDCZP _camera;
    private readonly ILogger<CameraRDWorker> _logger;

    public CameraRDWorker(RDCZP camera, ILogger<CameraRDWorker> logger)
    {
        _camera = camera;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _camera.ClientCallback += OnCameraEvent;
        try
        {
            await _camera.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _camera.ClientCallback -= OnCameraEvent;
            _logger.LogInformation("Camera RD worker đã dừng.");
        }
    }

    private void OnCameraEvent(eRDCZPState state, string data)
    {
        switch (state)
        {
            case eRDCZPState.Connected:
                _logger.LogInformation("Camera RD đã kết nối: {Message}", data);
                break;
            case eRDCZPState.Disconnected:
                _logger.LogWarning("Camera RD mất kết nối: {Message}", data);
                break;
            case eRDCZPState.Reconnecting:
                _logger.LogInformation("Camera RD đang kết nối lại: {Message}", data);
                break;
            case eRDCZPState.Received:
                _logger.LogInformation("Camera RD nhận Datamatrix: {Data}", data);
                break;
        }
    }
}
