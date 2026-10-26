using Microsoft.Azure.Functions.Worker;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public sealed class RefreshUserSearchIndexWorker(IUserSearchIndexRefresh refresh)
{
    [Function("RefreshUserSearchIndexWorker")]
    public Task Run([QueueTrigger(RefreshRequestQueue.Name)] string message, CancellationToken cancellationToken) =>
        refresh.Run(cancellationToken);
}
