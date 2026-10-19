using Azure.Core.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }
}
