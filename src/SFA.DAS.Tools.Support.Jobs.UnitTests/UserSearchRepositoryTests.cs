using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Moq;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture]
public class UserSearchRepositoryTests
{
    [Test]
    public void DefinesEmailAnalyzersAndSearchableFields()
    {
        var index = UserSearchDocument.CreateIndex("test");
        var email = index.Fields.Single(f => f.Name == "EmailAddress");
        Assert.That(email.IndexAnalyzerName!.Value.ToString(), Is.EqualTo("email_index"));
        Assert.That(email.SearchAnalyzerName!.Value.ToString(), Is.EqualTo("email_search"));
        Assert.That(email.AnalyzerName, Is.Null);
        Assert.That(index.Fields.Single(f => f.Name == "Id").IsKey, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "DisplayName").IsSortable, Is.True);
        Assert.That(index.Fields.Single(f => f.Name == "DisplayName").IsSearchable, Is.True);
        var indexAnalyzer = (CustomAnalyzer)index.Analyzers.Single(a => a.Name == "email_index");
        var searchAnalyzer = (CustomAnalyzer)index.Analyzers.Single(a => a.Name == "email_search");
        Assert.That(indexAnalyzer.TokenizerName, Is.EqualTo(LexicalTokenizerName.UaxUrlEmail));
        Assert.That(indexAnalyzer.TokenFilters.Select(f => f.ToString()), Is.EqualTo(new[] { "lowercase", "email_prefix" }));
        Assert.That(searchAnalyzer.TokenFilters.Select(f => f.ToString()), Is.EqualTo(new[] { "lowercase" }));
        var edge = (EdgeNGramTokenFilter)index.TokenFilters.Single();
        Assert.That(edge.MinGram, Is.EqualTo(3));
        Assert.That(edge.MaxGram, Is.EqualTo(255));
    }

    [TestCase(false, 2)]
    [TestCase(true, 1)]
    public void PartialUploadResponsesCannotBeReportedAsSuccess(bool succeeded, int returnedCount)
    {
        var search = new Mock<SearchClient>();
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetSearchClient("new-index")).Returns(search.Object);
        var results = Enumerable.Range(0, returnedCount).Select(i => SearchModelFactory.IndexingResult(i.ToString(), null, succeeded, 200));
        search.Setup(c => c.MergeOrUploadDocumentsAsync(It.IsAny<IEnumerable<UserSearchDocument>>(),
                It.Is<IndexDocumentsOptions>(o => o.ThrowOnAnyError == true), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SearchModelFactory.IndexDocumentsResult(results), Mock.Of<Response>()));
        var repository = new UserSearchIndexRepository(admin.Object);
        Assert.ThrowsAsync<InvalidOperationException>(() => repository.Upload("new-index", [new() { Id = "1" }, new() { Id = "2" }], default));
    }

    [TestCase(401)]
    [TestCase(403)]
    [TestCase(503)]
    public void AliasLookupErrorsAreNotTreatedAsAnAbsentAlias(int status)
    {
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetAliasAsync(UserSearchIndexRepository.AliasName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(status, "Unavailable"));
        Assert.ThrowsAsync<RequestFailedException>(() => new UserSearchIndexRepository(admin.Object).GetAlias(default));
    }

    [Test]
    public async Task MissingAliasIsAllowedForFirstRefresh()
    {
        var admin = new Mock<SearchIndexClient>();
        admin.Setup(c => c.GetAliasAsync(UserSearchIndexRepository.AliasName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));
        Assert.That((await new UserSearchIndexRepository(admin.Object).GetAlias(default)).Target, Is.Null);
    }
}
