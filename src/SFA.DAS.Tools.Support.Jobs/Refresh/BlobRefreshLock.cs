using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace SFA.DAS.Tools.Support.Jobs.Refresh;

public interface IRefreshLease : IAsyncDisposable
{
    CancellationToken CancellationToken { get; }
}

public interface IRefreshLock
{
    Task<IRefreshLease> Acquire(CancellationToken cancellationToken);
}

public sealed class BlobRefreshLock(BlobContainerClient container, ILogger<BlobRefreshLock> logger, TimeProvider timeProvider) : IRefreshLock
{
    public async Task<IRefreshLease> Acquire(CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient("user-search-index-refresh");
        try
        {
            await blob.UploadAsync(BinaryData.FromString(""), overwrite: false, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409) { }

        var client = blob.GetBlobLeaseClient();
        // Conflicts throw, so the queue retries rather than acknowledging an unfinished refresh.
        await client.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
        return new RenewableLease(client, logger, timeProvider, cancellationToken);
    }

    private sealed class RenewableLease : IRefreshLease
    {
        private readonly BlobLeaseClient client;
        private readonly ILogger logger;
        private readonly TimeProvider timeProvider;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenSource work;
        private readonly Task renewal;
        public CancellationToken CancellationToken => work.Token;

        public RenewableLease(BlobLeaseClient client, ILogger logger, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            this.client = client;
            this.logger = logger;
            this.timeProvider = timeProvider;
            work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            renewal = Renew();
        }

        private async Task Renew()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20), timeProvider);
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    // Stop work before the lease can expire if renewal cannot be confirmed.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await client.RenewAsync(cancellationToken: deadline.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                work.Cancel();
                logger.LogError(exception, "User search refresh lock renewal failed; cancelling the refresh");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await renewal;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ReleaseAsync(cancellationToken: deadline.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "User search refresh lock could not be released; its lease will expire");
            }
            work.Dispose();
            stop.Dispose();
        }
    }
}
