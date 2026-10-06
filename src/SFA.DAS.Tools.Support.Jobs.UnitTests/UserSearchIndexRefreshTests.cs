using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Configuration;
using SFA.DAS.Tools.Support.Jobs.Profiles;
using SFA.DAS.Tools.Support.Jobs.Refresh;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class UserSearchIndexRefreshTests
{
    private const string A = "tools-support-users-20260901020000";
    private const string B = "tools-support-users-20260902020000";
    private const string C = "tools-support-users-20260903020000";
    private Mock<IEmployerProfilesClient> profiles = null!;
    private Mock<IRefreshLock> refreshLock = null!;
    private TestIndexes indexes = null!;
    private TestClock clock = null!;
    private TestLease lease = null!;
    private UserProfile[] users = null!;

    [SetUp]
    public void SetUp()
    {
        profiles = new();
        refreshLock = new();
        indexes = new();
        clock = new();
        lease = new();
        refreshLock.Setup(x => x.Acquire(It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        users = Enumerable.Range(1, 3).Select(i => new UserProfile(Guid.NewGuid(), $"user{i}@example.com", $"User {i}")).ToArray();
        profiles.Setup(x => x.GetUsers(It.IsAny<int>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, int size, CancellationToken _) => new UserProfilesPage(users.Skip((page - 1) * size).Take(size).ToList(), users.Length, page, size));
    }

    private UserSearchIndexRefresh Create(int verificationAttempts = 1) => new(profiles.Object, indexes, refreshLock.Object,
        Options.Create(new JobsOptions { PageSize = 2, VerificationAttempts = verificationAttempts, VerificationDelaySeconds = 0 }),
        clock, NullLogger<UserSearchIndexRefresh>.Instance);

    [Test]
    public async Task UploadsAllPagesAndVerifiesBeforePromoting()
    {
        await Create().Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Documents[B].Select(x => x.Id), Is.EquivalentTo(users.Select(x => x.Id.ToString("D"))));
        Assert.That(indexes.Documents[B][0].EmailAddress, Is.EqualTo(users[0].Email));
        Assert.That(indexes.Documents[B][0].DisplayName, Is.EqualTo(users[0].DisplayName));
        Assert.That(indexes.Events, Is.EqualTo(new[] { "create", "upload", "upload", "count", "promote" }));
        profiles.Verify(x => x.GetUsers(3, 2, It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(lease.Disposed, Is.True);
    }

    [Test]
    public async Task RetainsPreviousUntilNextSuccessfulRefresh()
    {
        indexes.Seed(A);
        await Create().Run(default);
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A, B }));
        clock.UtcNow = new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero);
        await Create().Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(C));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { B, C }));
    }

    [Test]
    public void UploadFailureLeavesAliasAndPreviousIndexesUnchanged()
    {
        indexes.Seed(A);
        indexes.UploadException = new InvalidOperationException("Partial upload failed");
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [Test]
    public void FailureAfterFirstPageNeverPromotes()
    {
        indexes.Seed(A);
        profiles.Setup(x => x.GetUsers(2, 2, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Unavailable"));
        Assert.ThrowsAsync<HttpRequestException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
    }

    [Test]
    public void CountMismatchDoesNotSwitchAlias()
    {
        indexes.Seed(A);
        indexes.CountOverride = () => 1;
        Assert.ThrowsAsync<InvalidOperationException>(() => Create(3).Run(default));
        Assert.That(indexes.Events.Count(x => x == "count"), Is.EqualTo(3));
        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
    }

    [Test]
    public async Task WaitsForEventualSearchCountBeforePromoting()
    {
        var counts = new Queue<long>([0, 1, 3]);
        indexes.CountOverride = counts.Dequeue;
        await Create(3).Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(counts, Is.Empty);
    }

    [Test]
    public void DuplicateIdsAcrossPagesFailVerification()
    {
        users[2] = users[0];
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.Null);
        Assert.That(indexes.Documents, Is.Empty);
    }

    [Test]
    public void EmptyUserIdFailsVerification()
    {
        users[0] = users[0] with { Id = Guid.Empty };
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [TestCase("total")]
    [TestCase("page")]
    [TestCase("size")]
    [TestCase("short")]
    [TestCase("null")]
    [TestCase("negative")]
    public void RejectsInconsistentSourcePages(string fault)
    {
        var page = new UserProfilesPage([users[2]], 3, 2, 2);
        page = fault switch
        {
            "total" => page with { TotalCount = 4 },
            "page" => page with { PageNumber = 1 },
            "size" => page with { PageSize = 1000 },
            "short" => page with { UserProfiles = [] },
            "null" => page with { UserProfiles = null },
            _ => page with { TotalCount = -1 }
        };
        profiles.Setup(x => x.GetUsers(2, 2, It.IsAny<CancellationToken>())).ReturnsAsync(page);
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
    }

    [Test]
    public async Task EmptySourceCanProduceVerifiedEmptyIndex()
    {
        users = [];
        await Create().Run(default);
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Documents[B], Is.Empty);
        Assert.That(indexes.Events, Does.Not.Contain("upload"));
    }

    [Test]
    public async Task ExactPageBoundaryDoesNotFetchExtraPage()
    {
        users = users.Take(2).ToArray();
        await Create().Run(default);
        profiles.Verify(x => x.GetUsers(2, 2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CleansOnlyDatedUserIndexesAndRetainsActualPriorTarget()
    {
        indexes.Documents["tools-support-users-20260902010000"] = []; // failed refresh newer than A
        indexes.Documents["reservations-20260801020000"] = [];
        indexes.Documents["tools-support-users-unrelated"] = [];
        indexes.Seed(A);
        await Create().Run(default);
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A, B, "reservations-20260801020000", "tools-support-users-unrelated" }));
    }

    [Test]
    public void NeverDeletesIndexIfAliasUpdateSucceededButResponseWasLost()
    {
        indexes.Seed(A);
        indexes.FailAfterPromotion = true;
        Assert.ThrowsAsync<TimeoutException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A, B }));
    }

    [Test]
    public void AliasUpdateFailureLeavesServingIndexIntact()
    {
        indexes.Seed(A);
        indexes.FailBeforePromotion = true;
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(A));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { A }));
    }

    [Test]
    public void NameCollisionDoesNotUploadOrDeleteServingIndex()
    {
        indexes.Seed(B);
        Assert.ThrowsAsync<ArgumentException>(() => Create().Run(default));
        Assert.That(indexes.Alias, Is.EqualTo(B));
        Assert.That(indexes.Documents.Keys, Is.EquivalentTo(new[] { B }));
        Assert.That(indexes.Events, Does.Not.Contain("upload"));
    }

    [Test]
    public void LockConflictDoesNotCreateIndex()
    {
        refreshLock.Setup(x => x.Acquire(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Lease held"));
        Assert.ThrowsAsync<InvalidOperationException>(() => Create().Run(default));
        Assert.That(indexes.Documents, Is.Empty);
    }

    [Test]
    public void LostLeasePreventsAliasChange()
    {
        indexes.CountOverride = () => { lease.Cancellation.Cancel(); return 3; };
        Assert.ThrowsAsync<OperationCanceledException>(() => Create().Run(default));
        Assert.That(indexes.Events, Does.Not.Contain("promote"));
        Assert.That(lease.Disposed, Is.True);
    }

    [TestCase("tools-support-users-20260929020000", true)]
    [TestCase("tools-support-users-20269999020000", false)]
    [TestCase("tools-support-users-202609290200001", false)]
    [TestCase("reservations-20260929020000", false)]
    public void CleanupRecognisesOnlyOwnedDatedIndexes(string name, bool expected) =>
        Assert.That(UserSearchIndexRefresh.IsUserIndex(name), Is.EqualTo(expected));

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 2, 2, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestLease : IRefreshLease
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken CancellationToken => Cancellation.Token;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class TestIndexes : IUserSearchIndexRepository
    {
        public string? Alias;
        public Dictionary<string, List<UserSearchDocument>> Documents { get; } = new();
        public List<string> Events { get; } = [];
        public Exception? UploadException;
        public Func<long>? CountOverride;
        public bool FailAfterPromotion;
        public bool FailBeforePromotion;
        public void Seed(string name) { Documents[name] = []; Alias = name; }
        public Task<IndexAlias> GetAlias(CancellationToken token) => Task.FromResult(new IndexAlias(Alias));
        public Task Create(string name, CancellationToken token) { Events.Add("create"); Documents.Add(name, []); return Task.CompletedTask; }
        public Task Upload(string name, IReadOnlyCollection<UserSearchDocument> documents, CancellationToken token)
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
            if (FailBeforePromotion) throw new InvalidOperationException("Alias update failed");
            Assert.That(previous.Target, Is.EqualTo(Alias));
            Alias = name;
            if (FailAfterPromotion) throw new TimeoutException();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> List(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Documents.Keys.ToArray());
        public Task Delete(string name, CancellationToken token) { Assert.That(name, Is.Not.EqualTo(Alias)); Documents.Remove(name); return Task.CompletedTask; }
    }
}
