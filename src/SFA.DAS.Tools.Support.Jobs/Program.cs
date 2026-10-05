using Microsoft.Extensions.Hosting;
using SFA.DAS.Tools.Support.Jobs.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration((_, builder) => builder.AddJobsConfiguration())
    .ConfigureServices((context, services) => services.AddJobsServices(context.Configuration))
    .Build();

await host.RunAsync();
