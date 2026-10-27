using System.Net;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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

    private static ServiceProvider Provider(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddDasLogging()
            .AddConfigurationOptions(configuration)
            .AddServiceRegistration(configuration)
            .AddRefreshServices();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider AccountProvider(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = Settings();
        settings["EnvironmentName"] = "LOCAL";
        settings["ToolsSupportJobs:EmployerAccountsApiIdentifierUri"] = "";
        settings["ToolsSupportJobs:EmployerProfilesApiIdentifierUri"] = "";
        return Provider(settings, services =>
        {
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new Handler(respond)));
            services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        });
    }

    [TestCase(408)]
    [TestCase(429)]
    [TestCase(503)]
    public async Task AccountsClientRetriesTransientResponses(int status)
    {
        var attempts = 0;
        await using var provider = AccountProvider(_ => AccountResponse(
            Interlocked.Increment(ref attempts) == 1 ? (HttpStatusCode)status : HttpStatusCode.OK));

        var account = await provider.GetRequiredService<IEmployerAccountsClient>().GetAccount(1, default);

        Assert.That(account.AccountId, Is.EqualTo(1));
        Assert.That(attempts, Is.EqualTo(2));
    }

    [Test]
    public async Task AccountsClientRetriesTransientConnectionFailures()
    {
        var attempts = 0;
        await using var provider = AccountProvider(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("Temporary connection failure.");
            return AccountResponse(HttpStatusCode.OK);
        });

        var account = await provider.GetRequiredService<IEmployerAccountsClient>().GetAccount(1, default);

        Assert.That(account.AccountId, Is.EqualTo(1));
        Assert.That(attempts, Is.EqualTo(2));
    }

    [TestCase(400)]
    [TestCase(401)]
    [TestCase(404)]
    public async Task AccountsClientDoesNotRetryPermanentFailures(int status)
    {
        var attempts = 0;
        await using var provider = AccountProvider(_ =>
        {
            Interlocked.Increment(ref attempts);
            return AccountResponse((HttpStatusCode)status);
        });

        Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRequiredService<IEmployerAccountsClient>().GetAccount(1, default));

        Assert.That(attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task AccountsClientPropagatesFailureAfterRetriesAreExhausted()
    {
        var attempts = 0;
        await using var provider = AccountProvider(_ =>
        {
            Interlocked.Increment(ref attempts);
            return AccountResponse(HttpStatusCode.ServiceUnavailable);
        });

        Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRequiredService<IEmployerAccountsClient>().GetAccount(1, default));

        Assert.That(attempts, Is.EqualTo(4));
    }

    [Test]
    public async Task AccountsResilienceDoesNotChangeTheProfilesClient()
    {
        var attempts = 0;
        await using var provider = AccountProvider(_ =>
        {
            Interlocked.Increment(ref attempts);
            return AccountResponse(HttpStatusCode.ServiceUnavailable);
        });

        Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRequiredService<IEmployerProfilesClient>().GetUsers(1, 1000, default));

        Assert.That(attempts, Is.EqualTo(1));
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

    private static HttpResponseMessage AccountResponse(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("""{"accountId":1,"publicHashedAccountId":"ABC1","dasAccountName":"Account 1","payeSchemes":[],"legalEntities":[]}""")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
