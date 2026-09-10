using ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;
using MasanCZCodeBackend.Configuration;
using MasanCZCodeBackend.DataPool;
using MasanCZCodeBackend.ProductionOrder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MasanCZCodeBackend;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Bind "Storage" section từ appsettings.json vào StorageOptions.
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

        builder.Services.AddSingleton(new RDCZP("127.0.0.1", 9000));
        builder.Services.AddSingleton<DataPoolModule>();
        builder.Services.AddSingleton<PODatabase>();

        builder.Services.AddHostedService<ReceivedDataProcessor>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}
