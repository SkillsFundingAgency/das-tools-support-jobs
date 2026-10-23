using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshUserSearchIndexTests
{
    [Test]
    public async Task TimerQueuesSameRefresh()
    {
        var queue = new Mock<IRefreshRequestQueue>();
        await new RefreshUserSearchIndex(queue.Object, NullLogger<RefreshUserSearchIndex>.Instance).Run(new TimerInfo(), default);
        queue.Verify(q => q.Enqueue(It.IsAny<CancellationToken>()), Times.Once);
    }
}
