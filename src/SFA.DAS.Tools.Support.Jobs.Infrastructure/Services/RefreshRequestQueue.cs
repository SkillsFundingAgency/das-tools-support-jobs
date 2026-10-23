using Azure.Storage.Queues;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

public sealed class RefreshRequestQueue(QueueClient queue) : IRefreshRequestQueue
{
    public const string Name = "sfa-das-tools-support-user-index-refresh";

    public async Task Enqueue(CancellationToken cancellationToken)
    {
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await queue.SendMessageAsync("refresh", cancellationToken);
    }
}
