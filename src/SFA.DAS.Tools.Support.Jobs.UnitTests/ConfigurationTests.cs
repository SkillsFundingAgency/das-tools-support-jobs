using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Configuration;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Profiles;
using SFA.DAS.Tools.Support.Jobs.Refresh;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class ConfigurationTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["EnvironmentName"] = "AT",
        ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
        ["ToolsSupportJobs:AzureSearchBaseUrl"] = "https://test.search.windows.net",
        ["ToolsSupportJobs:EmployerProfilesApiBaseUrl"] = "https://profiles.example/",
        ["ToolsSupportJobs:EmployerProfilesApiIdentifierUri"] = "https://profiles.example"
    };

    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging().AddJobsServices(configuration).BuildServiceProvider();
    }

    [Test]
    public async Task DeployedConfigurationResolvesTheRefreshDependencies()
    {
        await using var provider = Provider(Settings());
        Assert.That(provider.GetRequiredService<IOptions<JobsOptions>>().Value.PageSize, Is.EqualTo(1000));
        Assert.That(provider.GetRequiredService<TokenCredential>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<SearchIndexClient>().Endpoint.Host, Is.EqualTo("test.search.windows.net"));
        Assert.That(provider.GetRequiredService<BlobContainerClient>().Name, Is.EqualTo("tools-support-jobs-locks"));
        Assert.That(provider.GetRequiredService<QueueClient>().Name, Is.EqualTo(RefreshRequestQueue.Name));
        Assert.That(provider.GetRequiredService<IRefreshRequestQueue>(), Is.TypeOf<RefreshRequestQueue>());
        Assert.That(provider.GetRequiredService<IRefreshLock>(), Is.TypeOf<BlobRefreshLock>());
        Assert.That(provider.GetRequiredService<IUserSearchIndexRepository>(), Is.TypeOf<UserSearchIndexRepository>());
        Assert.That(provider.GetRequiredService<IEmployerProfilesClient>(), Is.TypeOf<EmployerProfilesClient>());
        Assert.That(provider.GetRequiredService<IUserSearchIndexRefresh>(), Is.TypeOf<UserSearchIndexRefresh>());
        Assert.That(provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules.Any(r =>
            r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider"), Is.False);
    }

    [TestCase("AzureSearchBaseUrl", "http://search.example")]
    [TestCase("AzureSearchBaseUrl", "")]
    [TestCase("EmployerProfilesApiBaseUrl", "http://profiles.example")]
    [TestCase("EmployerProfilesApiBaseUrl", "invalid")]
    [TestCase("EmployerProfilesApiIdentifierUri", "")]
    [TestCase("PageSize", "0")]
    [TestCase("PageSize", "1001")]
    [TestCase("VerificationAttempts", "0")]
    [TestCase("VerificationAttempts", "121")]
    [TestCase("VerificationDelaySeconds", "0")]
    [TestCase("VerificationDelaySeconds", "61")]
    public void RejectsInvalidDeploymentSettings(string key, string value)
    {
        var settings = Settings();
        settings["ToolsSupportJobs:" + key] = value;
        using var provider = Provider(settings);
        Assert.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<JobsOptions>>().Value);
    }

    [TestCase("LOCAL")]
    [TestCase("DEV")]
    public void LocalProfilesApiCanRunWithoutAnEntraIdentifier(string environment)
    {
        var settings = Settings();
        settings["EnvironmentName"] = environment;
        settings["ToolsSupportJobs:EmployerProfilesApiIdentifierUri"] = "";
        settings["ToolsSupportJobs:EmployerProfilesApiBaseUrl"] = "http://localhost:5000";
        using var provider = Provider(settings);
        Assert.DoesNotThrow(() => _ = provider.GetRequiredService<IOptions<JobsOptions>>().Value);
        Assert.DoesNotThrow(() => new ConfigurationBuilder().AddInMemoryCollection(settings).AddJobsConfiguration().Build());
    }

    [Test]
    public void MissingStorageCannotStartAWorker()
    {
        var settings = Settings();
        settings.Remove("AzureWebJobsStorage");
        Assert.Throws<InvalidOperationException>(() => Provider(settings));
    }

    [TestCase("ConfigNames", null)]
    [TestCase("ConfigNames", "$(ConfigNames)")]
    [TestCase("ConfigNames", ", ,")]
    [TestCase("ConfigurationStorageConnectionString", null)]
    [TestCase("EnvironmentName", null)]
    public void MissingTableSettingsFailWithTheSettingName(string key, string? value)
    {
        var settings = Settings();
        settings["ConfigNames"] = "SFA.DAS.Tools.Support.Jobs_1.0";
        settings["ConfigurationStorageConnectionString"] = "UseDevelopmentStorage=true";
        settings[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder().AddInMemoryCollection(settings).AddJobsConfiguration());
        Assert.That(error!.Message, Does.Contain(key));
    }
}
