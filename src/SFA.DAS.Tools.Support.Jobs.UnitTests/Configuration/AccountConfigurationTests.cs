using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Extensions;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Functions.Extensions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Extensions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;
using SFA.DAS.Tools.Support.Jobs.Services;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Configuration;

[TestFixture]
public class AccountConfigurationTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["EnvironmentName"] = "AT",
        ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
        ["ToolsSupportJobs:AzureSearchBaseUrl"] = "https://test.search.windows.net",
        ["ToolsSupportJobs:EmployerProfilesApiBaseUrl"] = "https://profiles.example/",
        ["ToolsSupportJobs:EmployerProfilesApiIdentifierUri"] = "https://profiles.example",
        ["ToolsSupportJobs:EmployerAccountsApiBaseUrl"] = "https://accounts.example/",
        ["ToolsSupportJobs:EmployerAccountsApiIdentifierUri"] = "https://accounts.example"
    };

    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddDasLogging()
            .AddConfigurationOptions(configuration)
            .AddServiceRegistration(configuration)
            .AddRefreshServices()
            .BuildServiceProvider();
    }

    [Test]
    public async Task AccountDependenciesUseSeparateQueuesAndLocksWhileUserDependenciesStillResolve()
    {
        await using var provider = Provider(Settings());
        Assert.That(provider.GetRequiredService<IEmployerAccountsClient>(), Is.TypeOf<EmployerAccountsClient>());
        Assert.That(provider.GetRequiredService<IAccountSearchIndexRepository>(), Is.TypeOf<AccountSearchIndexRepository>());
        Assert.That(provider.GetRequiredService<IAccountSearchIndexRefresh>(), Is.TypeOf<AccountSearchIndexRefresh>());
        Assert.That(provider.GetRequiredService<IUserSearchIndexRefresh>(), Is.TypeOf<UserSearchIndexRefresh>());
        Assert.That(provider.GetRequiredKeyedService<QueueClient>(AccountSearchIndexRepository.AliasName).Name, Is.EqualTo(RefreshAccountSearchIndex.QueueName));
        Assert.That(provider.GetRequiredKeyedService<IRefreshRequestQueue>(AccountSearchIndexRepository.AliasName),
            Is.Not.SameAs(provider.GetRequiredService<IRefreshRequestQueue>()));
        Assert.That(provider.GetRequiredKeyedService<IRefreshLock>(AccountSearchIndexRepository.AliasName),
            Is.Not.SameAs(provider.GetRequiredService<IRefreshLock>()));
    }

    [TestCase("EmployerAccountsApiBaseUrl", "")]
    [TestCase("EmployerAccountsApiBaseUrl", "http://accounts.example")]
    [TestCase("EmployerAccountsApiBaseUrl", "invalid")]
    [TestCase("EmployerAccountsApiIdentifierUri", "")]
    public async Task MissingOrInvalidAccountConfigurationDoesNotBlockUserRefresh(string key, string value)
    {
        var settings = Settings();
        settings["ToolsSupportJobs:" + key] = value;
        await using var provider = Provider(settings);
        Assert.DoesNotThrow(() => _ = provider.GetRequiredService<IUserSearchIndexRefresh>());
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IAccountSearchIndexRefresh>());
    }

    [TestCase("LOCAL")]
    [TestCase("DEV")]
    public async Task LocalAccountsApiCanUseLoopbackWithoutAnEntraIdentifier(string environment)
    {
        var settings = Settings();
        settings["EnvironmentName"] = environment;
        settings["ToolsSupportJobs:EmployerAccountsApiBaseUrl"] = "http://localhost:5001";
        settings["ToolsSupportJobs:EmployerAccountsApiIdentifierUri"] = "";
        await using var provider = Provider(settings);
        Assert.DoesNotThrow(() => _ = provider.GetRequiredService<IAccountSearchIndexRefresh>());
    }
}
