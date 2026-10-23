using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public sealed class RefreshAccountSearchIndex(
    [FromKeyedServices(AccountSearchIndexRepository.AliasName)] IRefreshRequestQueue queue,
    ILogger<RefreshAccountSearchIndex> logger)
{
    public const string QueueName = RefreshRequestQueue.AccountQueueName;

    [Function("RefreshAccountSearchIndex")]
    public async Task Run([TimerTrigger("%RefreshAccountSearchIndexSchedule%", UseMonitor = true)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        await queue.Enqueue(cancellationToken);
        logger.LogInformation("Daily account search index refresh queued");
    }
}
