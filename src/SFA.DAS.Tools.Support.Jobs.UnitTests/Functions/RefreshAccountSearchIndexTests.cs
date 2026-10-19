using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshAccountSearchIndexTests
{
    [Test]
    public async Task TimerQueuesTheAccountWorkerUsingItsOwnSchedule()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        await new RefreshAccountSearchIndex(queue.Object, NullLogger<RefreshAccountSearchIndex>.Instance).Run(new TimerInfo(), default);
        queue.Verify(q => q.Enqueue(It.IsAny<CancellationToken>()), Times.Once);
        var timer = (TimerTriggerAttribute)typeof(RefreshAccountSearchIndex).GetMethod("Run")!.GetParameters()[0]
            .GetCustomAttributes(typeof(TimerTriggerAttribute), false).Single();
        Assert.That(timer.Schedule, Is.EqualTo("%RefreshAccountSearchIndexSchedule%"));
        Assert.That(RefreshAccountSearchIndex.QueueName, Is.Not.EqualTo(RefreshRequestQueue.Name));
    }
}
