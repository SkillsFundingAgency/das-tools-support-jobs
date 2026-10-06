using Microsoft.Extensions.Configuration;
using SFA.DAS.Configuration.AzureTableStorage;

namespace SFA.DAS.Tools.Support.Jobs.Configuration;

public static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddJobsConfiguration(this IConfigurationBuilder builder)
    {
        // Core Tools exports local.settings.json Values as environment variables.
        builder.AddEnvironmentVariables();
        var settings = builder.Build();
        if (settings["EnvironmentName"] is "LOCAL" or "DEV" && string.IsNullOrWhiteSpace(settings["ConfigNames"]))
            return builder;

        var keys = Required(settings, "ConfigNames").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length == 0) throw new InvalidOperationException("ConfigNames must contain a configuration name.");
        var connection = Required(settings, "ConfigurationStorageConnectionString");
        var environment = Required(settings, "EnvironmentName");
        builder.AddAzureTableStorage(options =>
        {
            options.ConfigurationKeys = keys;
            options.StorageConnectionString = connection;
            options.EnvironmentName = environment;
            options.ConfigurationNameIncludesVersionNumber = true;
            options.PreFixConfigurationKeys = false;
        });
        return builder.AddEnvironmentVariables();
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration[name];
        return string.IsNullOrWhiteSpace(value) || value.Contains("$(", StringComparison.Ordinal)
            ? throw new InvalidOperationException($"{name} is required for configuration-table loading.")
            : value;
    }
}
