using ClassLibraryMT.Communications.RussiaDatamatrixCZProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MasanCZCodeBackend;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(new RDCZP("127.0.0.1", 9000));
        builder.Services.AddHostedService<ReceivedDataProcessor>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}
