using Microsoft.Azure.Functions.Worker;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshAccountSearchIndexWorkerTests
{
    [Test]
    public void WorkerPropagatesErrorsForIndependentAccountQueueRetries()
    {
        var refresh = new Mock<IAccountSearchIndexRefresh>();
        refresh.Setup(r => r.Run(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshAccountSearchIndexWorker(refresh.Object).Run("refresh", default));
        var trigger = (QueueTriggerAttribute)typeof(RefreshAccountSearchIndexWorker).GetMethod("Run")!.GetParameters()[0]
            .GetCustomAttributes(typeof(QueueTriggerAttribute), false).Single();
        Assert.That(trigger.QueueName, Is.EqualTo(RefreshAccountSearchIndex.QueueName));
    }
}
