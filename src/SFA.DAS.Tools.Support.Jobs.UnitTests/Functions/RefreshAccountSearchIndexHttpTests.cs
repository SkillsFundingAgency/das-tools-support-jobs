using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshAccountSearchIndexHttpTests
{
    [Test]
    public async Task HttpIsFunctionKeyProtectedPostAndAcknowledgesOnlyAQueuedRefresh()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        var result = await new RefreshAccountSearchIndexHttp(queue.Object).Run(new DefaultHttpContext().Request, default);
        Assert.That(result, Is.TypeOf<AcceptedResult>());
        queue.Verify(q => q.Enqueue(It.IsAny<CancellationToken>()), Times.Once);
        var trigger = (HttpTriggerAttribute)typeof(RefreshAccountSearchIndexHttp).GetMethod("Run")!.GetParameters()[0]
            .GetCustomAttributes(typeof(HttpTriggerAttribute), false).Single();
        Assert.That(trigger.AuthLevel, Is.EqualTo(AuthorizationLevel.Function));
        Assert.That(trigger.Methods, Is.EqualTo(new[] { "post" }));
        Assert.That(trigger.Route, Is.EqualTo("account-search-index/refresh"));
    }

    [Test]
    public void FailedEnqueueDoesNotReturnAccepted()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshAccountSearchIndexHttp(queue.Object).Run(new DefaultHttpContext().Request, default));
    }
}
