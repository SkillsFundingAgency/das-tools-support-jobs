using Microsoft.Extensions.Hosting;
using SFA.DAS.Tools.Support.Jobs.Extensions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Extensions;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration((_, builder) => builder.BuildDasConfiguration())
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        services.AddDasLogging();
        services.AddConfigurationOptions(configuration);
        services.AddServiceRegistration(configuration);
        services.AddRefreshServices();
    })
    .Build();

await host.RunAsync();
