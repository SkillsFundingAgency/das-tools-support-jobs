using Azure.Core.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Extensions;

public static class ServiceRegistrationExtensions
{
    public static IServiceCollection AddServiceRegistration(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddKeyedSingleton<QueueClient>(AccountSearchIndexRepository.AliasName, (_, _) =>
            new QueueClient(storage, RefreshRequestQueue.AccountQueueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
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
        }).AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(100);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(8);
            options.CircuitBreaker.SamplingDuration = options.AttemptTimeout.Timeout * 2;
        });
        return services;
    }
}
