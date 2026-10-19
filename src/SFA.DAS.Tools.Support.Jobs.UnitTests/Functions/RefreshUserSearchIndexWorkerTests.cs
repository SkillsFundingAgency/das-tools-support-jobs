using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Functions;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Functions;

[TestFixture]
public class RefreshUserSearchIndexWorkerTests
{
    [Test]
    public void WorkerPropagatesFailureForQueueRetry()
    {
        var refresh = new Mock<IUserSearchIndexRefresh>();
        refresh.Setup(r => r.Run(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => new RefreshUserSearchIndexWorker(refresh.Object).Run("refresh", default));
    }
}
