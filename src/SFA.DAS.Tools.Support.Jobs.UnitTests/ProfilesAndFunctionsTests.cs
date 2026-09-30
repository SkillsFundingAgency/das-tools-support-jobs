using System.Net;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Configuration;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Profiles;
using SFA.DAS.Tools.Support.Jobs.Refresh;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class ProfilesAndFunctionsTests
{
    [Test]
    public async Task ReadsApiContractAndRequestsTokenForProfilesApi()
    {
        var credential = new Mock<TokenCredential>();
        credential.Setup(c => c.GetTokenAsync(It.Is<TokenRequestContext>(r => r.Scopes.Single() == "https://profiles.example/.default"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        var userId = Guid.NewGuid();
        using var handler = new TestHandler(request =>
        {
            Assert.That(request.RequestUri!.PathAndQuery, Is.EqualTo("/api/users?pageSize=1000&pageNumber=2"));
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer test-token"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"userProfiles":[{"id":"{{userId}}","email":"user.name+test@example.com","displayName":"Test User"}],"totalCount":1001,"pageNumber":2,"pageSize":1000}""") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://profiles.example/") };
        var client = new EmployerProfilesClient(http, credential.Object, Options.Create(new JobsOptions { EmployerProfilesApiIdentifierUri = "https://profiles.example/" }));
        var result = await client.GetUsers(2, 1000, default);
        Assert.That(result.TotalCount, Is.EqualTo(1001));
        Assert.That(result.UserProfiles!.Single().Id, Is.EqualTo(userId));
        Assert.That(result.UserProfiles!.Single().Email, Is.EqualTo("user.name+test@example.com"));
    }

    [Test]
    public void MissingSourceCountCannotBeMistakenForEmptySource()
    {
        using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"userProfiles":[],"pageNumber":1,"pageSize":1000}""")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new EmployerProfilesClient(http, Mock.Of<TokenCredential>(), Options.Create(new JobsOptions()));
        Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.GetUsers(1, 1000, default));
    }

    [Test]
    public void ProfilesHttpFailureIsPropagated()
    {
        using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new EmployerProfilesClient(http, Mock.Of<TokenCredential>(), Options.Create(new JobsOptions()));
        Assert.ThrowsAsync<HttpRequestException>(() => client.GetUsers(1, 1000, default));
    }

    [Test]
    public async Task HttpReturnsAcceptedOnlyAfterRequestIsQueued()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        var function = new RefreshUserSearchIndexHttp(queue.Object);
        var result = await function.Run(new DefaultHttpContext().Request, default);
        Assert.That(result, Is.TypeOf<AcceptedResult>());
        queue.Verify(q => q.Enqueue(It.IsAny<CancellationToken>()), Times.Once);
        var trigger = (HttpTriggerAttribute)typeof(RefreshUserSearchIndexHttp).GetMethod("Run")!.GetParameters()[0]
            .GetCustomAttributes(typeof(HttpTriggerAttribute), false).Single();
        Assert.That(trigger.AuthLevel, Is.EqualTo(AuthorizationLevel.Function));
        Assert.That(trigger.Methods, Is.EqualTo(new[] { "post" }));
    }

    [Test]
    public void HttpDoesNotAcknowledgeFailedEnqueue()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshUserSearchIndexHttp(queue.Object).Run(new DefaultHttpContext().Request, default));
    }

    [Test]
    public async Task TimerQueuesSameRefresh()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        await new RefreshUserSearchIndex(queue.Object, NullLogger<RefreshUserSearchIndex>.Instance).Run(new TimerInfo(), default);
        queue.Verify(q => q.Enqueue(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void WorkerPropagatesFailureForQueueRetry()
    {
        var refresh = new Mock<IUserSearchIndexRefresh>();
        refresh.Setup(r => r.Run(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshUserSearchIndexWorker(refresh.Object).Run("refresh", default));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
