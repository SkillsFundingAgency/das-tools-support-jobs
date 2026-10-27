using System.Net;
using System.Net.Http.Headers;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Polly.Timeout;
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

    private static ServiceProvider AccountProvider(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        AccountProvider((request, _) => Task.FromResult(respond(request)));

    private static ServiceProvider AccountProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeProvider? timeProvider = null)
    {
        var settings = Settings();
        settings["EnvironmentName"] = "LOCAL";
        settings["ToolsSupportJobs:EmployerAccountsApiIdentifierUri"] = "";
        settings["ToolsSupportJobs:EmployerProfilesApiIdentifierUri"] = "";
        return Provider(settings, services =>
        {
            if (timeProvider is not null) services.AddSingleton(timeProvider);
            services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => new Handler(respond)));
            services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        });
    }

    [Test]
    public async Task PagedAccountsRequestCanCompleteAfterNinetySeconds()
    {
        var timeProvider = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = AccountProvider(async (request, token) =>
        {
            Assert.That(request.RequestUri!.PathAndQuery, Is.EqualTo("/api/accounts?pageSize=1000&pageNumber=1"));
            var delay = Task.Delay(TimeSpan.FromSeconds(90), timeProvider, token);
            started.TrySetResult();
            await delay;
            return AccountsPageResponse();
        }, timeProvider);

        var response = provider.GetRequiredService<IEmployerAccountsClient>().GetAccounts(1, 1000, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(90));
        var page = await response.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(page.Data!.Single().AccountId, Is.EqualTo(1));
        Assert.That(page.Page, Is.EqualTo(1));
    }

    [Test]
    public async Task PagedAccountsRequestRetriesAfterTheAttemptTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        await using var provider = AccountProvider(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var pending = Task.Delay(Timeout.InfiniteTimeSpan, token);
                started.TrySetResult();
                await pending;
            }
            return AccountsPageResponse();
        }, timeProvider);

        var response = provider.GetRequiredService<IEmployerAccountsClient>().GetAccounts(1, 1000, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            timeProvider.Advance(TimeSpan.FromSeconds(100));
            var page = await response.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(page.Data!.Single().AccountId, Is.EqualTo(1));
            Assert.That(attempts, Is.EqualTo(2));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Test]
    public async Task AccountsRequestCannotRetryBeyondTheTotalTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        await using var provider = AccountProvider((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }, timeProvider);

        var response = provider.GetRequiredService<IEmployerAccountsClient>().GetAccounts(1, 1000, cancellation.Token);
        try
        {
            timeProvider.Advance(TimeSpan.FromMinutes(8));

            Assert.ThrowsAsync<TimeoutRejectedException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(attempts, Is.EqualTo(1));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Test]
    public async Task AccountsClientLeavesTimeoutControlToTheResilienceHandler()
    {
        await using var provider = AccountProvider(_ => AccountResponse(HttpStatusCode.OK));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEmployerAccountsClient));

        Assert.That(client.Timeout, Is.EqualTo(Timeout.InfiniteTimeSpan));
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

    private static HttpResponseMessage AccountsPageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"data":[{"accountId":1}],"page":1,"totalPages":1}""")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
