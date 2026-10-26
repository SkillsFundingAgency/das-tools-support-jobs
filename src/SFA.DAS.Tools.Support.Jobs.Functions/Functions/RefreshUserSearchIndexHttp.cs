using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

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
