using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Refresh;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class BlobRefreshLockTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreatesOrReusesTheLockAndRenewsUntilDisposed(bool existingBlob)
    {
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new List<string>();
        using var handler = new StorageHandler(request =>
        {
            if (request.Headers.TryGetValues("x-ms-lease-action", out var values))
            {
                var action = values.Single();
                actions.Add(action);
                if (action == "renew") renewed.SetResult();
                return LeaseResponse(action == "acquire" ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            return LeaseResponse(existingBlob && !request.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)
                ? HttpStatusCode.Conflict : HttpStatusCode.Created);
        });
        var time = new FakeTimeProvider();
        var lease = await Create(handler, time).Acquire(default);
        time.Advance(TimeSpan.FromSeconds(20));
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(lease.CancellationToken.IsCancellationRequested, Is.False);
        await lease.DisposeAsync();
        Assert.That(actions, Is.EqualTo(new[] { "acquire", "renew", "release" }));
    }

    [Test]
    public async Task LostRenewalCancelsWorkEvenIfReleaseAlsoFails()
    {
        using var handler = new StorageHandler(request => LeaseResponse(
            request.Headers.TryGetValues("x-ms-lease-action", out var values) && values.Single() != "acquire"
                ? HttpStatusCode.Forbidden : HttpStatusCode.Created));
        var time = new FakeTimeProvider();
        await using var lease = await Create(handler, time).Acquire(default);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease.CancellationToken.Register(() => cancelled.TrySetResult());
        time.Advance(TimeSpan.FromSeconds(20));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(lease.CancellationToken.IsCancellationRequested, Is.True);
    }

    [Test]
    public void HeldLeaseCannotStartAnotherRefresh()
    {
        using var handler = new StorageHandler(request => LeaseResponse(
            request.Headers.Contains("x-ms-lease-action") ? HttpStatusCode.Conflict : HttpStatusCode.Created));
        Assert.ThrowsAsync<RequestFailedException>(() => Create(handler, new FakeTimeProvider()).Acquire(default));
    }

    private static BlobRefreshLock Create(HttpMessageHandler handler, TimeProvider time) => new(
        new BlobContainerClient(new Uri("https://test.blob.core.windows.net/locks"), new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)), Retry = { MaxRetries = 0 }
        }), NullLogger<BlobRefreshLock>.Instance, time);

    private static HttpResponseMessage LeaseResponse(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.Add("x-ms-lease-id", "00000000-0000-0000-0000-000000000001");
        response.Headers.Add("x-ms-error-code", "BlobAlreadyExists");
        return response;
    }

    private sealed class StorageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
