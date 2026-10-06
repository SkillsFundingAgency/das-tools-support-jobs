using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SFA.DAS.Tools.Support.Jobs.Refresh;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public interface IRefreshRequestQueue
{
    Task Enqueue(CancellationToken cancellationToken);
}

public sealed class RefreshRequestQueue(QueueClient queue) : IRefreshRequestQueue
{
    public const string Name = "sfa-das-tools-support-user-index-refresh";

    public async Task Enqueue(CancellationToken cancellationToken)
    {
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await queue.SendMessageAsync("refresh", cancellationToken);
    }
}

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

public sealed class RefreshUserSearchIndexHttp(IRefreshRequestQueue queue)
{
    [Function("RefreshUserSearchIndexHttp")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "user-search-index/refresh")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        await queue.Enqueue(cancellationToken);
        return new AcceptedResult { Value = new { Message = "User search index refresh queued." } };
    }
}

public sealed class RefreshUserSearchIndexWorker(IUserSearchIndexRefresh refresh)
{
    [Function("RefreshUserSearchIndexWorker")]
    public Task Run([QueueTrigger(RefreshRequestQueue.Name)] string message, CancellationToken cancellationToken) =>
        refresh.Run(cancellationToken);
}
