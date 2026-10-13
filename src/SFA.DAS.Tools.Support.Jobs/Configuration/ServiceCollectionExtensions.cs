using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Accounts;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Profiles;
using SFA.DAS.Tools.Support.Jobs.Refresh;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJobsServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobsOptions>().Bind(configuration.GetSection(JobsOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.AzureSearchBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https", "AzureSearchBaseUrl must be HTTPS.")
            .Validate(o => Uri.TryCreate(o.EmployerProfilesApiBaseUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" || (uri.IsLoopback && uri.Scheme == "http")), "EmployerProfilesApiBaseUrl must be HTTPS or loopback HTTP.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.EmployerProfilesApiIdentifierUri) ||
                configuration["EnvironmentName"] is "LOCAL" or "DEV", "EmployerProfilesApiIdentifierUri is required outside LOCAL/DEV.")
            .Validate(o => o.PageSize is >= 1 and <= 1000, "PageSize must be between 1 and 1000.")
            .Validate(o => o.VerificationAttempts is >= 1 and <= 120 && o.VerificationDelaySeconds is >= 1 and <= 60,
                "VerificationAttempts and VerificationDelaySeconds must be within supported bounds.")
            .ValidateOnStart();
        services.AddSingleton<TokenCredential, DefaultAzureCredential>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new SearchIndexClient(new Uri(sp.GetRequiredService<IOptions<JobsOptions>>().Value.AzureSearchBaseUrl),
            sp.GetRequiredService<TokenCredential>(), new SearchClientOptions
            {
                Serializer = new JsonObjectSerializer(new System.Text.Json.JsonSerializerOptions())
            }));
        var storage = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is required for the refresh queue and lock.");
        services.AddSingleton(new BlobContainerClient(storage, "tools-support-jobs-locks"));
        services.AddSingleton(new QueueClient(storage, RefreshRequestQueue.Name, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
        services.AddSingleton<IRefreshRequestQueue, RefreshRequestQueue>();
        services.AddSingleton<IRefreshLock, BlobRefreshLock>();
        services.AddSingleton<IUserSearchIndexRepository, UserSearchIndexRepository>();
        services.AddHttpClient<IEmployerProfilesClient, EmployerProfilesClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<JobsOptions>>().Value.EmployerProfilesApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(100);
        });
        services.AddTransient<IUserSearchIndexRefresh, UserSearchIndexRefresh>();
        services.AddOptions<JobsOptions>(AccountSearchIndexRepository.AliasName).Bind(configuration.GetSection(JobsOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.EmployerAccountsApiBaseUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" || (uri.IsLoopback && uri.Scheme == "http")), "EmployerAccountsApiBaseUrl must be HTTPS or loopback HTTP.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.EmployerAccountsApiIdentifierUri) ||
                configuration["EnvironmentName"] is "LOCAL" or "DEV", "EmployerAccountsApiIdentifierUri is required outside LOCAL/DEV.");
        services.AddKeyedSingleton<QueueClient>(AccountSearchIndexRepository.AliasName, (_, _) =>
            new QueueClient(storage, RefreshAccountSearchIndex.QueueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
        services.AddKeyedSingleton<IRefreshRequestQueue>(AccountSearchIndexRepository.AliasName, (sp, _) =>
            new RefreshRequestQueue(sp.GetRequiredKeyedService<QueueClient>(AccountSearchIndexRepository.AliasName)));
        services.AddKeyedSingleton<IRefreshLock>(AccountSearchIndexRepository.AliasName, (sp, _) =>
            new BlobRefreshLock(sp.GetRequiredService<BlobContainerClient>(), sp.GetRequiredService<ILogger<BlobRefreshLock>>(),
                sp.GetRequiredService<TimeProvider>(), "account-search-index-refresh"));
        services.AddSingleton<IAccountSearchIndexRepository, AccountSearchIndexRepository>();
        services.AddHttpClient<IEmployerAccountsClient, EmployerAccountsClient>((sp, client) =>
        {
            var accountOptions = sp.GetRequiredService<IOptionsMonitor<JobsOptions>>().Get(AccountSearchIndexRepository.AliasName);
            client.BaseAddress = new Uri(accountOptions.EmployerAccountsApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(100);
        });
        services.AddTransient<IAccountSearchIndexRefresh, AccountSearchIndexRefresh>();
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
