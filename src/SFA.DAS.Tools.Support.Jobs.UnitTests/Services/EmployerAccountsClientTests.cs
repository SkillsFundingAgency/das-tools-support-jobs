using System.Net;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Services;

[TestFixture]
public class EmployerAccountsClientTests
{
    [Test]
    public async Task ReadsAccountPagingDetailsAndLegalEntityContractsWithAnAccountsToken()
    {
        var credential = new Mock<TokenCredential>();
        credential.Setup(c => c.GetTokenAsync(It.Is<TokenRequestContext>(r => r.Scopes.Single() == "https://accounts.example/.default"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("account-token", DateTimeOffset.UtcNow.AddHours(1)));
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer account-token"));
            Assert.That(request.Headers.GetValues("X-Version"), Is.EqualTo(new[] { "1" }));
            var body = paths.Count switch
            {
                1 => """{"data":[{"accountId":123,"accountName":"Example","publicAccountHashId":"ABC123"}],"page":2,"totalPages":2}""",
                2 => """{"accountId":123,"dasAccountName":"Example","publicHashedAccountId":"ABC123","payeSchemes":[{"id":"123/AB456","href":"https://ignored.example"}],"legalEntities":[{"id":"10"}]}""",
                _ => """{"legalEntityId":10,"name":"Example Limited"}"""
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://accounts.example/") };
        var client = new EmployerAccountsClient(http, credential.Object, Options.Create(new JobsOptions { EmployerAccountsApiIdentifierUri = "https://accounts.example/" }));
        var page = await client.GetAccounts(2, 1000, default);
        var detail = await client.GetAccount(123, default);
        var legalEntity = await client.GetLegalEntity(123, 10, default);
        Assert.That(page.Page, Is.EqualTo(2));
        Assert.That(page.TotalPages, Is.EqualTo(2));
        Assert.That(page.Data!.Single().AccountId, Is.EqualTo(123));
        Assert.That(detail.PublicHashedAccountId, Is.EqualTo("ABC123"));
        Assert.That(detail.PayeSchemes!.Single().Id, Is.EqualTo("123/AB456"));
        Assert.That(legalEntity.Name, Is.EqualTo("Example Limited"));
        Assert.That(paths, Is.EqualTo(new[] { "/api/accounts?pageSize=1000&pageNumber=2", "/api/accounts/123", "/api/accounts/123/legalentities/10" }));
    }

    [TestCase("""{"data":[],"page":1}""")]
    [TestCase("""{"data":[],"totalPages":1}""")]
    [TestCase("""{"page":1,"totalPages":1}""")]
    [TestCase("""{"data":[{}],"page":1,"totalPages":1}""")]
    public void MissingPagingFieldsAreNotTreatedAsAnEmptySuccessfulSource(string json)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new EmployerAccountsClient(http, Mock.Of<TokenCredential>(), Options.Create(new JobsOptions()));
        Assert.ThrowsAsync<JsonException>(() => client.GetAccounts(1, 1000, default));
    }

    [TestCase(401)]
    [TestCase(404)]
    [TestCase(503)]
    public void AccountApiFailuresPropagate(int status)
    {
        using var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new EmployerAccountsClient(http, Mock.Of<TokenCredential>(), Options.Create(new JobsOptions()));
        Assert.ThrowsAsync<HttpRequestException>(() => client.GetAccount(1, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
