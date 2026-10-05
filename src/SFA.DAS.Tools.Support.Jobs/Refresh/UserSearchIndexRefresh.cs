using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Configuration;
using SFA.DAS.Tools.Support.Jobs.Profiles;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.Refresh;

public interface IUserSearchIndexRefresh
{
    Task Run(CancellationToken cancellationToken);
}

public sealed class UserSearchIndexRefresh(
    IEmployerProfilesClient profiles,
    IUserSearchIndexRepository indexes,
    IRefreshLock refreshLock,
    IOptions<JobsOptions> options,
    TimeProvider timeProvider,
    ILogger<UserSearchIndexRefresh> logger) : IUserSearchIndexRefresh
{
    private const string IndexPrefix = UserSearchIndexRepository.AliasName + "-";

    public async Task Run(CancellationToken cancellationToken)
    {
        await using var lease = await refreshLock.Acquire(cancellationToken);
        var token = lease.CancellationToken;
        var previous = await indexes.GetAlias(token);
        var name = IndexPrefix + timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        // Create, rather than upsert: a name collision must never reuse a serving index.
        await indexes.Create(name, token);
        try
        {
            var totalCount = await UploadAllPages(name, token);
            await Verify(name, totalCount, token);
            token.ThrowIfCancellationRequested();
            await indexes.Promote(name, previous, token);
            logger.LogInformation("User search alias switched to {IndexName}; previous {PreviousIndex}; verified {DocumentCount} documents",
                name, previous.Target, totalCount);
        }
        catch
        {
            // A timed-out alias update might have succeeded remotely. Re-read before deleting.
            await RemoveFailedIndex(name, token);
            throw;
        }

        // Retain the actual former alias target, not simply the second newest dated index.
        foreach (var oldIndex in await indexes.List(token))
        {
            if (oldIndex == name || oldIndex == previous.Target || !IsUserIndex(oldIndex)) continue;
            token.ThrowIfCancellationRequested();
            if ((await indexes.GetAlias(token)).Target != name)
                throw new InvalidOperationException("Alias changed during cleanup; stopping without deleting further indexes.");
            await indexes.Delete(oldIndex, token);
            logger.LogInformation("Deleted retired user index {IndexName}", oldIndex);
        }
        logger.LogInformation("User search refresh completed for {IndexName}", name);
    }

    private async Task<int> UploadAllPages(string indexName, CancellationToken token)
    {
        int? totalCount = null;
        var seen = new HashSet<Guid>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await profiles.GetUsers(pageNumber, options.Value.PageSize, token);
            totalCount ??= page.TotalCount;
            if (page.UserProfiles is null || page.PageNumber != pageNumber || page.PageSize != options.Value.PageSize ||
                page.TotalCount < 0 || page.TotalCount != totalCount)
                throw new InvalidOperationException($"Profiles API returned inconsistent paging metadata at page {pageNumber}.");

            var expectedCount = Math.Min(options.Value.PageSize, totalCount.Value - seen.Count);
            if (page.UserProfiles.Count != expectedCount)
                throw new InvalidOperationException($"Profiles API returned an incomplete page at page {pageNumber}.");
            foreach (var user in page.UserProfiles)
            {
                if (user.Id == Guid.Empty || !seen.Add(user.Id))
                    throw new InvalidOperationException($"Profiles API returned a missing or duplicate user ID at page {pageNumber}.");
            }
            if (page.UserProfiles.Count > 0)
                await indexes.Upload(indexName, page.UserProfiles.Select(UserSearchDocument.From).ToArray(), token);
            logger.LogInformation("Uploaded user page {PageNumber}: {PageCount} documents; {UploadedCount}/{TotalCount} to {IndexName}",
                pageNumber, page.UserProfiles.Count, seen.Count, totalCount, indexName);
            if (seen.Count == totalCount) return totalCount.Value;
        }
    }

    private async Task Verify(string name, int expected, CancellationToken token)
    {
        long actual = -1;
        for (var attempt = 1; attempt <= options.Value.VerificationAttempts; attempt++)
        {
            actual = await indexes.Count(name, token);
            if (actual == expected) return;
            if (attempt < options.Value.VerificationAttempts)
                await Task.Delay(TimeSpan.FromSeconds(options.Value.VerificationDelaySeconds), timeProvider, token);
        }
        throw new InvalidOperationException($"User index verification failed: expected {expected} documents, found {actual}.");
    }

    private async Task RemoveFailedIndex(string name, CancellationToken token)
    {
        try
        {
            if ((await indexes.GetAlias(token)).Target != name)
                await indexes.Delete(name, token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove failed index {IndexName}; a successful refresh will retry cleanup", name);
        }
    }

    public static bool IsUserIndex(string name) => name.StartsWith(IndexPrefix, StringComparison.Ordinal) &&
        DateTime.TryParseExact(name[IndexPrefix.Length..], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
