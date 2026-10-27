using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Azure.Search.Documents;
using Azure;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests.Services;

[TestFixture]
public class AccountSearchRepositoryTests
{
    [Test]
    public void DefinesSearchableAccountFieldsAndReferenceAnalyzers()
    {
        var index = AccountSearchDocument.CreateIndex("test");
        Assert.That(index.Fields.Single(f => f.Name == "Id").IsKey, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "AccountId").IsFilterable, Is.True);
        foreach (var name in new[] { "PublicHashedId", "PayeSchemes" })
        {
            var field = index.Fields.Single(f => f.Name == name);
            Assert.That(field.IsSearchable, Is.True);
            Assert.That(field.IsFilterable, Is.True);
            Assert.That(field.IndexAnalyzerName!.Value.ToString(), Is.EqualTo("account_reference_index"));
            Assert.That(field.SearchAnalyzerName!.Value.ToString(), Is.EqualTo("account_reference_search"));
        }
        Assert.That(index.Fields.Single(f => f.Name == "AccountName").IsSearchable, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "AccountName").IsSortable, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "LegalEntityNames").IsSearchable, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "PayeSchemes").Type,
            Is.EqualTo(SearchFieldDataType.Collection(SearchFieldDataType.String)));
        var indexAnalyzer = (CustomAnalyzer)index.Analyzers.Single(a => a.Name == "account_reference_index");
        var searchAnalyzer = (CustomAnalyzer)index.Analyzers.Single(a => a.Name == "account_reference_search");
        Assert.That(indexAnalyzer.TokenizerName, Is.EqualTo(LexicalTokenizerName.Keyword));
        Assert.That(searchAnalyzer.TokenizerName, Is.EqualTo(LexicalTokenizerName.Keyword));
        Assert.That(indexAnalyzer.TokenFilters.Select(f => f.ToString()), Is.EqualTo(new[] { "lowercase", "account_reference_prefix" }));
        Assert.That(searchAnalyzer.TokenFilters.Select(f => f.ToString()), Is.EqualTo(new[] { "lowercase" }));
        var edge = (EdgeNGramTokenFilter)index.TokenFilters.Single();
        Assert.That(edge.MinGram, Is.EqualTo(3));
        Assert.That(edge.MaxGram, Is.EqualTo(255));
    }

    [Test]
    public void MapsPublicIdAndPreservesPayePunctuationAndNamesWithoutDuplicateValues()
    {
        var account = new AccountDetail(123, "ABC123", "Example Ltd",
            [new("123/AB456"), new("123/AB456"), new("456/CD789")], []);
        var document = AccountSearchDocument.From(account, ["Example Ltd", "example ltd", "Another Company"]);
        Assert.That(document.Id, Is.EqualTo("123"));
        Assert.That(document.AccountId, Is.EqualTo(123));
        Assert.That(document.PublicHashedId, Is.EqualTo("ABC123"));
        Assert.That(document.AccountName, Is.EqualTo("Example Ltd"));
        Assert.That(document.PayeSchemes, Is.EqualTo(new[] { "123/AB456", "456/CD789" }));
        Assert.That(document.LegalEntityNames, Is.EqualTo(new[] { "Example Ltd", "Another Company" }));
    }
    [TestCase(false, 2)]
    [TestCase(true, 1)]
    public void PartialUploadResponsesCannotBeReportedAsSuccess(bool succeeded, int returnedCount)
    {
        var search = new Mock<SearchClient>();
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetSearchClient("new-index")).Returns(search.Object);
        var results = Enumerable.Range(0, returnedCount).Select(i => SearchModelFactory.IndexingResult(i.ToString(), null, succeeded, 200));
        search.Setup(c => c.MergeOrUploadDocumentsAsync(It.IsAny<IEnumerable<AccountSearchDocument>>(),
                It.Is<IndexDocumentsOptions>(o => o.ThrowOnAnyError == true), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SearchModelFactory.IndexDocumentsResult(results), Mock.Of<Response>()));
        var repository = new AccountSearchIndexRepository(admin.Object);
        Assert.ThrowsAsync<InvalidOperationException>(() => repository.Upload("new-index", [new() { Id = "1" }, new() { Id = "2" }], default));
    }

    [TestCase(401)]
    [TestCase(403)]
    [TestCase(503)]
    public void AliasLookupErrorsAreNotTreatedAsAnAbsentAlias(int status)
    {
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetAliasAsync(AccountSearchIndexRepository.AliasName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(status, "Unavailable"));
        Assert.ThrowsAsync<RequestFailedException>(() => new AccountSearchIndexRepository(admin.Object).GetAlias(default));
    }

    [Test]
    public async Task MissingAliasIsAllowedForFirstRefresh()
    {
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetAliasAsync(AccountSearchIndexRepository.AliasName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));
        Assert.That((await new AccountSearchIndexRepository(admin.Object).GetAlias(default)).Target, Is.Null);
    }
}
