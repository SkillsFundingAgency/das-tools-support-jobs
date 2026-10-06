using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Queues;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class SearchSdkTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task AliasPromotionUsesTheObservedEtagOrRequiresAnAbsentAlias(bool existing)
    {
        var writes = 0;
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """{"name":"tools-support-users","indexes":["old"],"@odata.etag":"\"version-one\""}""");
            writes++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.That(body.RootElement.GetProperty("indexes")[0].GetString(), Is.EqualTo("new"));
            Assert.That(request.Method, Is.EqualTo(existing ? HttpMethod.Put : HttpMethod.Post));
            if (existing) Assert.That(request.Headers.IfMatch.Single().ToString(), Is.EqualTo("\"version-one\""));
            return Json(existing ? HttpStatusCode.OK : HttpStatusCode.Created, """{"name":"tools-support-users","indexes":["new"]}""");
        });
        var repository = Repository(handler);
        var alias = existing ? await repository.GetAlias(default) : new IndexAlias(null);
        await repository.Promote("new", alias, default);
        Assert.That(writes, Is.EqualTo(1));
    }

    [Test]
    public async Task UploadUsesMergeOrUploadAndReadsTheServiceCountAndIndexList()
    {
        var uploads = 0;
        var deletes = 0;
        using var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/docs/search.index", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.That(body.RootElement.GetProperty("value")[0].GetProperty("@search.action").GetString(), Is.EqualTo("mergeOrUpload"));
                uploads++;
                return Json(HttpStatusCode.OK, """{"value":[{"key":"one","status":true,"statusCode":200}]}""");
            }
            if (path.EndsWith("/$count", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "1");
            if (request.Method == HttpMethod.Delete)
            {
                deletes++;
                return Json(HttpStatusCode.NoContent, "");
            }
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """{"value":[{"name":"new"},{"name":"old"}]}""");
            return Json(HttpStatusCode.Created, await request.Content!.ReadAsStringAsync());
        });
        var repository = Repository(handler);
        await repository.Create("new", default);
        await repository.Upload("new", [new UserSearchDocument { Id = "one" }], default);
        Assert.That(await repository.Count("new", default), Is.EqualTo(1));
        Assert.That(await repository.List(default), Is.EqualTo(new[] { "new", "old" }));
        await repository.Delete("old", default);
        Assert.That(uploads, Is.EqualTo(1));
        Assert.That(deletes, Is.EqualTo(1));
    }

    [Test]
    public async Task QueueCreatesStorageQueueAndSendsBase64RefreshMessage()
    {
        var requests = new List<HttpMethod>();
        using var handler = new Handler(async request =>
        {
            requests.Add(request.Method);
            if (request.Method == HttpMethod.Post)
            {
                Assert.That(await request.Content!.ReadAsStringAsync(), Does.Contain(Convert.ToBase64String("refresh"u8.ToArray())));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("<QueueMessagesList><QueueMessage><MessageId>one</MessageId><InsertionTime>Fri, 02 Oct 2026 10:00:00 GMT</InsertionTime><ExpirationTime>Fri, 09 Oct 2026 10:00:00 GMT</ExpirationTime><PopReceipt>one</PopReceipt><TimeNextVisible>Fri, 02 Oct 2026 10:00:00 GMT</TimeNextVisible></QueueMessage></QueueMessagesList>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var client = new QueueClient(new Uri("https://test.queue.core.windows.net/refresh"), new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64,
            Transport = new HttpClientTransport(new HttpClient(handler)), Retry = { MaxRetries = 0 }
        });
        await new RefreshRequestQueue(client).Enqueue(default);
        Assert.That(requests, Is.EqualTo(new[] { HttpMethod.Put, HttpMethod.Post }));
    }

    private static UserSearchIndexRepository Repository(HttpMessageHandler handler) => new(new SearchIndexClient(
        new Uri("https://test.search.windows.net"), new AzureKeyCredential("test-key"), new SearchClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)), Retry = { MaxRetries = 0 }
        }));

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
