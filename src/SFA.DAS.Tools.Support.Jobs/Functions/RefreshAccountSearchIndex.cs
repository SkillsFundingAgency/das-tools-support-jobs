using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SFA.DAS.Tools.Support.Jobs.Refresh;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

public sealed class RefreshAccountSearchIndex(
    [FromKeyedServices(AccountSearchIndexRepository.AliasName)] IRefreshRequestQueue queue,
    ILogger<RefreshAccountSearchIndex> logger)
{
    public const string QueueName = "sfa-das-tools-support-account-index-refresh";

    [Function("RefreshAccountSearchIndex")]
    public async Task Run([TimerTrigger("%RefreshAccountSearchIndexSchedule%", UseMonitor = true)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        await queue.Enqueue(cancellationToken);
        logger.LogInformation("Daily account search index refresh queued");
    }
}

public sealed class RefreshAccountSearchIndexHttp(
    [FromKeyedServices(AccountSearchIndexRepository.AliasName)] IRefreshRequestQueue queue)
{
    [Function("RefreshAccountSearchIndexHttp")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "account-search-index/refresh")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        await queue.Enqueue(cancellationToken);
        return new AcceptedResult { Value = new { Message = "Account search index refresh queued." } };
    }
}

public sealed class RefreshAccountSearchIndexWorker(IAccountSearchIndexRefresh refresh)
{
    [Function("RefreshAccountSearchIndexWorker")]
    public Task Run([QueueTrigger(RefreshAccountSearchIndex.QueueName)] string message, CancellationToken cancellationToken) =>
        refresh.Run(cancellationToken);
}
