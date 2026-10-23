using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.Services;

public sealed class AccountSearchIndexRefresh(
    IEmployerAccountsClient accounts,
    IAccountSearchIndexRepository indexes,
    [FromKeyedServices(AccountSearchIndexRepository.AliasName)] IRefreshLock refreshLock,
    IOptions<JobsOptions> options,
    TimeProvider timeProvider,
    ILogger<AccountSearchIndexRefresh> logger) : IAccountSearchIndexRefresh
{
    private const string IndexPrefix = AccountSearchIndexRepository.AliasName + "-";

    public async Task Run(CancellationToken cancellationToken)
    {
        await using var lease = await refreshLock.Acquire(cancellationToken);
        var token = lease.CancellationToken;
        var previous = await indexes.GetAlias(token);
        var name = IndexPrefix + timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        await indexes.Create(name, token);
        try
        {
            var totalCount = await UploadAllPages(name, token);
            await Verify(name, totalCount, token);
            token.ThrowIfCancellationRequested();
            await indexes.Promote(name, previous, token);
            logger.LogInformation("Account search alias switched to {IndexName}; previous {PreviousIndex}; verified {DocumentCount} documents",
                name, previous.Target, totalCount);
        }
        catch
        {
            await RemoveFailedIndex(name, token);
            throw;
        }

        foreach (var oldIndex in await indexes.List(token))
        {
            if (oldIndex == name || oldIndex == previous.Target || !IsAccountIndex(oldIndex)) continue;
            token.ThrowIfCancellationRequested();
            if ((await indexes.GetAlias(token)).Target != name)
                throw new InvalidOperationException("Alias changed during cleanup; stopping without deleting further indexes.");
            await indexes.Delete(oldIndex, token);
            logger.LogInformation("Deleted retired account index {IndexName}", oldIndex);
        }
        logger.LogInformation("Account search refresh completed for {IndexName}", name);
    }

    private async Task<int> UploadAllPages(string indexName, CancellationToken token)
    {
        int? totalPages = null;
        var seen = new HashSet<long>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await accounts.GetAccounts(pageNumber, options.Value.PageSize, token);
            totalPages ??= page.TotalPages;
            if (page.Data is null || page.Page != pageNumber || page.TotalPages < 1 || page.TotalPages != totalPages ||
                pageNumber > totalPages || page.Data.Count > options.Value.PageSize ||
                (pageNumber < totalPages && page.Data.Count != options.Value.PageSize))
                throw new InvalidOperationException($"Accounts API returned inconsistent or incomplete paging at page {pageNumber}.");

            var documents = new List<AccountSearchDocument>();
            foreach (var summary in page.Data)
            {
                token.ThrowIfCancellationRequested();
                if (summary.AccountId <= 0 || !seen.Add(summary.AccountId))
                    throw new InvalidOperationException($"Accounts API returned a missing or duplicate account ID at page {pageNumber}.");
                var account = await accounts.GetAccount(summary.AccountId, token);
                if (account.AccountId != summary.AccountId || account.PayeSchemes is null || account.LegalEntities is null ||
                    account.PayeSchemes.Any(paye => string.IsNullOrWhiteSpace(paye.Id)))
                    throw new InvalidOperationException("Accounts API returned inconsistent account details.");

                var names = new List<string>();
                var legalEntityIds = new HashSet<long>();
                foreach (var resource in account.LegalEntities)
                {
                    if (!long.TryParse(resource.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var legalEntityId) || legalEntityId <= 0)
                        throw new InvalidOperationException("Accounts API returned an invalid legal entity ID.");
                    if (!legalEntityIds.Add(legalEntityId)) continue;
                    var legalEntity = await accounts.GetLegalEntity(account.AccountId, legalEntityId, token);
                    if (legalEntity.LegalEntityId != legalEntityId)
                        throw new InvalidOperationException("Accounts API returned inconsistent legal entity details.");
                    names.Add(legalEntity.Name ?? "");
                }
                documents.Add(AccountSearchDocument.From(account, names));
            }
            if (documents.Count > 0) await indexes.Upload(indexName, documents, token);
            logger.LogInformation("Uploaded account page {PageNumber}/{TotalPages}: {PageCount} documents; {UploadedCount} total to {IndexName}",
                pageNumber, totalPages, documents.Count, seen.Count, indexName);
            if (pageNumber == totalPages) return seen.Count;
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
        throw new InvalidOperationException($"Account index verification failed: expected {expected} documents, found {actual}.");
    }

    private async Task RemoveFailedIndex(string name, CancellationToken token)
    {
        try
        {
            if ((await indexes.GetAlias(token)).Target != name) await indexes.Delete(name, token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove failed index {IndexName}; a successful refresh will retry cleanup", name);
        }
    }

    public static bool IsAccountIndex(string name) => name.StartsWith(IndexPrefix, StringComparison.Ordinal) &&
        DateTime.TryParseExact(name[IndexPrefix.Length..], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
