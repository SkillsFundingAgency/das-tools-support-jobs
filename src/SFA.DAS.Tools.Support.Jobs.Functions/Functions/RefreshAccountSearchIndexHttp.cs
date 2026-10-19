using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.Functions;

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
