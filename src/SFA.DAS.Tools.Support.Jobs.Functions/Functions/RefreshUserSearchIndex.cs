using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public sealed class RefreshUserSearchIndex(IRefreshRequestQueue queue, ILogger<RefreshUserSearchIndex> logger)
{
    [Function("RefreshUserSearchIndex")]
    public async Task Run([TimerTrigger("%RefreshUserSearchIndexSchedule%", UseMonitor = true)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        await queue.Enqueue(cancellationToken);
        logger.LogInformation("Daily user search index refresh queued");
    }
}
