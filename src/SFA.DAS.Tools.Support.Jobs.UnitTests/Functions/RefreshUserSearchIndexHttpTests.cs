using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshUserSearchIndexHttpTests
{
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
}
