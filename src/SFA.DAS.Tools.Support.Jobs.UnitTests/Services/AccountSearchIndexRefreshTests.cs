using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;
using SFA.DAS.Tools.Support.Jobs.Services;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Services;

[TestFixture]
public class AccountSearchIndexRefreshTests
{
    private const string A = "tools-support-accounts-20261001023000";
    private const string B = "tools-support-accounts-20261002023000";
    private const string C = "tools-support-accounts-20261003023000";
    private Mock<IEmployerAccountsClient> accounts = null!;
    private Mock<IRefreshLock> refreshLock = null!;
    private TestIndexes indexes = null!;
    private TestClock clock = null!;
    private TestLease lease = null!;
    private AccountSummary[] source = null!;

    [SetUp]
    public void SetUp()
    {
        accounts = new();
        refreshLock = new();
        indexes = new();
        clock = new();
        lease = new();
        refreshLock.Setup(x => x.Acquire(It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        source = Enumerable.Range(1, 3).Select(i => new AccountSummary(i)).ToArray();
        accounts.Setup(x => x.GetAccounts(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, int size, CancellationToken _) => new AccountsPage(source.Skip((page - 1) * size).Take(size).ToList(), page, source.Length / size + 1));
        accounts.Setup(x => x.GetAccount(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, CancellationToken _) => Detail(id));
        accounts.Setup(x => x.GetLegalEntity(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long _, long id, CancellationToken _) => new LegalEntity(id, $"Legal Entity {id}"));
    }

    private static AccountDetail Detail(long id) => new(id, $"ABC{id}", $"Account {id}", [new("123/AB456")], [new("10")]);
    private AccountSearchIndexRefresh Create(int attempts = 1, int pageSize = 2) => new(accounts.Object, indexes, refreshLock.Object,
        Options.Create(new JobsOptions { PageSize = pageSize, VerificationAttempts = attempts, VerificationDelaySeconds = 0 }),
        clock, NullLogger<AccountSearchIndexRefresh>.Instance);

    [Test]
    public async Task UploadsEveryPageAndAccountDetailBeforeVerifiedPromotion()
    {
        await Create().Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Documents[B].Select(x => x.AccountId), Is.EquivalentTo(new long[] { 1, 2, 3 }));
        var document = indexes.Documents[B][0];
        Assert.That(document.Id, Is.EqualTo("1"));
        Assert.That(document.PublicHashedId, Is.EqualTo("ABC1"));
        Assert.That(document.AccountName, Is.EqualTo("Account 1"));
        Assert.That(document.PayeSchemes, Is.EqualTo(new[] { "123/AB456" }));
        Assert.That(document.LegalEntityNames, Is.EqualTo(new[] { "Legal Entity 10" }));
        Assert.That(indexes.Events, Is.EqualTo(new[] { "create", "upload", "upload", "count", "promote" }));
        accounts.Verify(x => x.GetAccounts(3, 2, It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(lease.Disposed, Is.True);
    }

    [Test]
    public async Task ReadsTheApisExtraEmptyPageForAnExactPageSizeMultiple()
    {
        source = source.Take(2).ToArray();
        await Create().Run(default);
        accounts.Verify(x => x.GetAccounts(2, 2, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(indexes.Documents[B].Count, Is.EqualTo(2));
        Assert.That(indexes.Events.Count(x => x == "upload"), Is.EqualTo(1));
        Assert.That(indexes.Alias, Is.EqualTo(B));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EmptySourceDoesNotPromoteAnIndex(bool hasServingIndex)
    {
        var existingAccount = AccountSearchDocument.From(Detail(1), ["Existing legal entity"]);
        if (hasServingIndex)
        {
            indexes.Seed(A);
            indexes.Documents[A].Add(existingAccount);
        }
        source = [];

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));

        Assert.That(exception!.Message, Is.EqualTo("Accounts API returned no accounts; keeping the current index."));
        Assert.That(indexes.Alias, Is.EqualTo(hasServingIndex ? A : null));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(hasServingIndex ? new[] { A } : Array.Empty<string>()));
        if (hasServingIndex)
            Assert.That(indexes.Documents[A], Is.EqualTo(new[] { existingAccount }));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
        Assert.That(lease.Disposed, Is.True);
    }

    [Test]
    public async Task EnrichesAccountsConcurrentlyWithinTheLimitBeforeUploadingThePage()
    {
        source = Enumerable.Range(1, 12).Select(id => new AccountSummary(id)).ToArray();
        var batchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var sync = new object();
        accounts.Setup(x => x.GetAccount(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(async (long id, CancellationToken token) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (sync) maximumActive = Math.Max(maximumActive, current);
                if (current == 5) batchStarted.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    return Detail(id);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var refresh = Create(pageSize: 12).Run(default);
        try
        {
            await batchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref active), Is.EqualTo(5));
            Assert.That(indexes.Events, Does.Not.Contain("upload"));
            accounts.Verify(x => x.GetAccounts(2, 12, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            release.TrySetResult();
            await refresh;
        }

        Assert.That(maximumActive, Is.EqualTo(5));
        Assert.That(indexes.Documents[B].Select(document => document.AccountId), Is.EqualTo(source.Select(account => account.AccountId)));
        Assert.That(indexes.Documents[B].Select(document => document.AccountName), Is.EqualTo(source.Select(account => $"Account {account.AccountId}")));
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(lease.Disposed, Is.True);
    }

    [Test]
    public async Task EnrichmentFailureCancelsOtherAccountsAndKeepsTheServingIndex()
    {
        indexes.Seed(A);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = false;
        accounts.Setup(x => x.GetAccount(1, It.IsAny<CancellationToken>()))
            .Returns(async (long id, CancellationToken token) =>
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Detail(id);
                }
                finally
                {
                    siblingCancelled = token.IsCancellationRequested;
                }
            });
        accounts.Setup(x => x.GetAccount(2, It.IsAny<CancellationToken>()))
            .Returns(async (long _, CancellationToken token) =>
            {
                await firstStarted.Task.WaitAsync(token);
                throw new HttpRequestException("Account detail request failed.");
            });

        var refresh = Create().Run(default);
        try
        {
            Assert.ThrowsAsync<HttpRequestException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(siblingCancelled, Is.True);
            Assert.That(lease.Disposed, Is.True);
        }
        finally
        {
            lease.Cancellation.Cancel();
            try { await refresh; }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
        }

        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
        Assert.That(indexes.Events, Does.Not.Contain("upload"));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [Test]
    public async Task RetainsThePreviousIndexUntilTheNextSuccessfulRefresh()
    {
        indexes.Seed(A);
        await Create().Run(default);
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A, B }));
        clock.UtcNow = new(2026, 10, 3, 2, 30, 0, TimeSpan.Zero);
        await Create().Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(C));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { B, C }));
    }

    [Test]
    public async Task CleanupOnlyRemovesOwnedIndexesAndKeepsTheActualFormerAliasTarget()
    {
        indexes.Seed(A);
        indexes.Documents["tools-support-accounts-20261002010000"] = [];
        indexes.Documents["tools-support-accounts-unrelated"] = [];
        indexes.Documents["tools-support-users-20261001020000"] = [];
        indexes.Documents["reservations-20261001020000"] = [];
        await Create().Run(default);
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[]
            { A, B, "tools-support-accounts-unrelated", "tools-support-users-20261001020000", "reservations-20261001020000" }));
    }

    [TestCase("page")]
    [TestCase("detail")]
    [TestCase("legal entity")]
    [TestCase("upload")]
    public void SourceOrUploadFailureKeepsTheServingIndex(string failure)
    {
        indexes.Seed(A);
        if (failure == "page") accounts.Setup(x => x.GetAccounts(2, 2, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        if (failure == "detail") accounts.Setup(x => x.GetAccount(3, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        if (failure == "legal entity") accounts.Setup(x => x.GetLegalEntity(3, 10, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        if (failure == "upload") indexes.UploadException = new HttpRequestException();
        Assert.ThrowsAsync<HttpRequestException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [TestCase("null data")]
    [TestCase("wrong page")]
    [TestCase("changed pages")]
    [TestCase("no pages")]
    [TestCase("short page")]
    [TestCase("oversized page")]
    public void InvalidPagingCannotPromote(string failure)
    {
        var page = failure switch
        {
            "null data" => new AccountsPage(null, 1, 2),
            "wrong page" => new AccountsPage(source.Take(2).ToList(), 2, 2),
            "no pages" => new AccountsPage([], 1, 0),
            "short page" => new AccountsPage(source.Take(1).ToList(), 1, 2),
            "oversized page" => new AccountsPage(source.ToList(), 1, 2),
            _ => new AccountsPage(source.Skip(2).ToList(), 2, 3)
        };
        accounts.Setup(x => x.GetAccounts(failure == "changed pages" ? 2 : 1, 2, It.IsAny<CancellationToken>())).ReturnsAsync(page);
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1)]
    public void InvalidOrDuplicateAccountIdsCannotPromote(long id)
    {
        source[2] = new(id);
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [TestCase("wrong account")]
    [TestCase("missing paye")]
    [TestCase("empty paye")]
    [TestCase("missing legal entities")]
    [TestCase("invalid legal entity")]
    [TestCase("wrong legal entity")]
    public void IncompleteDetailsCannotPromote(string failure)
    {
        var detail = failure switch
        {
            "wrong account" => Detail(99),
            "missing paye" => Detail(1) with { PayeSchemes = null },
            "empty paye" => Detail(1) with { PayeSchemes = [new("")] },
            "missing legal entities" => Detail(1) with { LegalEntities = null },
            "invalid legal entity" => Detail(1) with { LegalEntities = [new("../10")] },
            _ => Detail(1)
        };
        accounts.Setup(x => x.GetAccount(1, It.IsAny<CancellationToken>())).ReturnsAsync(detail);
        if (failure == "wrong legal entity") accounts.Setup(x => x.GetLegalEntity(1, 10, It.IsAny<CancellationToken>())).ReturnsAsync(new LegalEntity(99, "Wrong"));
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [Test]
    public async Task AccountsWithoutPayeOrLegalEntitiesStillHaveDocuments()
    {
        accounts.Setup(x => x.GetAccount(1, It.IsAny<CancellationToken>())).ReturnsAsync(Detail(1) with { PayeSchemes = [], LegalEntities = [] });
        await Create().Run(default);
        Assert.That(indexes.Documents[B][0].PayeSchemes, Is.Empty);
        Assert.That(indexes.Documents[B][0].LegalEntityNames, Is.Empty);
    }

    [Test]
    public void CountMismatchNeverChangesTheAlias()
    {
        indexes.Seed(A);
        indexes.CountOverride = () => 1;
        Assert.ThrowsAsync<InvalidOperationException>(() => Create(3).Run(default));
        Assert.That(indexes.Events.Count(x => x == "count"), Is.EqualTo(3));
        Assert.That(indexes.Alias, Is.EqualTo(A));
    }

    [Test]
    public async Task DelayedCountConvergenceIsVerifiedBeforePromotion()
    {
        var counts = new Queue<long>([0, 1, 3]);
        indexes.CountOverride = counts.Dequeue;
        await Create(3).Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(counts, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AmbiguousPromotionDoesNotDeleteTheServingIndex(bool remotelySucceeded)
    {
        indexes.Seed(A);
        indexes.FailBeforePromotion = !remotelySucceeded;
        indexes.FailAfterPromotion = remotelySucceeded;
        Assert.ThrowsAsync<TimeoutException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(remotelySucceeded ? B : A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(remotelySucceeded ? new[] { A, B } : new[] { A }));
    }

    [Test]
    public void NameCollisionCannotUploadOrDeleteTheServingIndex()
    {
        indexes.Seed(B);
        Assert.ThrowsAsync<ArgumentException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Events, Does.Not.Contain("upload"));
    }

    [Test]
    public void LockConflictDoesNotCreateAnIndex()
    {
        refreshLock.Setup(x => x.Acquire(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Documents, Is.Empty);
    }

    [Test]
    public void LeaseLossPreventsPromotionAndDisposesTheLease()
    {
        indexes.CountOverride = () => { lease.Cancellation.Cancel(); return 3; };
        Assert.ThrowsAsync<OperationCanceledException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
        Assert.That(lease.Disposed, Is.True);
    }

    [TestCase("tools-support-accounts-20261001023000", true)]
    [TestCase("tools-support-accounts-20261301023000", false)]
    [TestCase("tools-support-accounts-202610010230001", false)]
    [TestCase("tools-support-users-20261001023000", false)]
    public void CleanupRecognisesOnlyDatedAccountIndexes(string name, bool expected) =>
        Assert.That(AccountSearchIndexRefresh.IsAccountIndex(name), Is.EqualTo(expected));

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 2, 2, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestLease : IRefreshLease
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken CancellationToken => Cancellation.Token;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class TestIndexes : IAccountSearchIndexRepository
    {
        public string? Alias;
        public Dictionary<string, List<AccountSearchDocument>> Documents { get; } = new();
        public List<string> Events { get; } = [];
        public Exception? UploadException;
        public Func<long>? CountOverride;
        public bool FailAfterPromotion;
        public bool FailBeforePromotion;
        public void Seed(string name) { Documents[name] = []; Alias = name; }
        public Task<IndexAlias> GetAlias(CancellationToken token) => Task.FromResult(new IndexAlias(Alias));
        public Task Create(string name, CancellationToken token) { Events.Add("create"); Documents.Add(name, []); return Task.CompletedTask; }
        public Task Upload(string name, IReadOnlyCollection<AccountSearchDocument> documents, CancellationToken token)
        {
            Events.Add("upload");
            if (UploadException is not null) throw UploadException;
            Documents[name].AddRange(documents);
            return Task.CompletedTask;
        }
        public Task<long> Count(string name, CancellationToken token) { Events.Add("count"); return Task.FromResult(CountOverride?.Invoke() ?? Documents[name].Count); }
        public Task Promote(string name, IndexAlias previous, CancellationToken token)
        {
            Events.Add("promote");
            if (FailBeforePromotion) throw new TimeoutException();
            Assert.That(previous.Target, Is.EqualTo(Alias));
            Alias = name;
            if (FailAfterPromotion) throw new TimeoutException();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> List(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Documents.Keys.ToArray());
        public Task Delete(string name, CancellationToken token) { Assert.That(name, Is.Not.EqualTo(Alias)); Documents.Remove(name); return Task.CompletedTask; }
    }
}
