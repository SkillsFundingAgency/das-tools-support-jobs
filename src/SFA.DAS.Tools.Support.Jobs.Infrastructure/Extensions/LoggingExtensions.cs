using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Extensions;

public static class LoggingExtensions
{
    public static IServiceCollection AddDasLogging(this IServiceCollection services)
    {
        services.AddApplicationInsightsTelemetryWorkerService().ConfigureFunctionsApplicationInsights();
        services.Configure<LoggerFilterOptions>(options =>
        {
            var defaultRule = options.Rules.FirstOrDefault(rule =>
                rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (defaultRule is not null) options.Rules.Remove(defaultRule);
        });
        return services;
    }
}
