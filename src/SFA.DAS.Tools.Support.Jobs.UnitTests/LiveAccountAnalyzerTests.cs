using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture, Explicit("Requires a development Azure Search endpoint and credentials; creates a temporary test index.")]
public class LiveAccountAnalyzerTests
{
    [TestCase("ABC123")]
    [TestCase("123/AB456")]
    public async Task AccountReferencesPreservePunctuationAndHaveLowercasePrefixes(string reference)
    {
        var endpoint = Environment.GetEnvironmentVariable("TOOLS_SUPPORT_TEST_SEARCH_URL");
        Assert.That(endpoint, Is.Not.Null.And.Not.Empty);
        var client = new SearchIndexClient(new Uri(endpoint!), new DefaultAzureCredential());
        var name = "tools-support-accounts-analyzer-test-" + Guid.NewGuid().ToString("N");
        await client.CreateIndexAsync(AccountSearchDocument.CreateIndex(name));
        try
        {
            var indexed = await client.AnalyzeTextAsync(name, new AnalyzeTextOptions(reference, new LexicalAnalyzerName("account_reference_index")));
            var expected = Enumerable.Range(3, reference.Length - 2).Select(length => reference[..length].ToLowerInvariant());
            Assert.That(indexed.Value.Select(token => token.Token), Is.EquivalentTo(expected));
            var searched = await client.AnalyzeTextAsync(name, new AnalyzeTextOptions(reference, new LexicalAnalyzerName("account_reference_search")));
            Assert.That(searched.Value.Select(token => token.Token), Is.EqualTo(new[] { reference.ToLowerInvariant() }));
        }
        finally
        {
            await client.DeleteIndexAsync(name);
        }
    }
}
