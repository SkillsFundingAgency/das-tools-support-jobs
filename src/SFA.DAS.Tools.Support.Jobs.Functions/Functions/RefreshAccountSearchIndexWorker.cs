using Microsoft.Azure.Functions.Worker;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public sealed class RefreshAccountSearchIndexWorker(IAccountSearchIndexRefresh refresh)
{
    [Function("RefreshAccountSearchIndexWorker")]
    public Task Run([QueueTrigger(RefreshAccountSearchIndex.QueueName)] string message, CancellationToken cancellationToken) =>
        refresh.Run(cancellationToken);
}
