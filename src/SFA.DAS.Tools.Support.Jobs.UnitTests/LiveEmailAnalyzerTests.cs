using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using NUnit.Framework;
using SFA.DAS.Tools.Support.Jobs.Search;

namespace SFA.DAS.Tools.Support.Jobs.UnitTests;

[TestFixture, Explicit("Requires a development Azure Search endpoint and credentials; creates a temporary test index.")]
public class LiveEmailAnalyzerTests
{
    [Test]
    public async Task EmailIsPreservedAndLowercasedWithPrefixesFromThreeCharacters()
    {
        var endpoint = Environment.GetEnvironmentVariable("TOOLS_SUPPORT_TEST_SEARCH_URL");
        Assert.That(endpoint, Is.Not.Null.And.Not.Empty, "Set TOOLS_SUPPORT_TEST_SEARCH_URL to a development Search service.");
        var client = new SearchIndexClient(new Uri(endpoint!), new DefaultAzureCredential());
        var name = "tools-support-users-analyzer-test-" + Guid.NewGuid().ToString("N");
        await client.CreateIndexAsync(UserSearchDocument.CreateIndex(name));
        try
        {
            const string email = "User.Name+Test@Example.com";
            var indexed = await client.AnalyzeTextAsync(name, new AnalyzeTextOptions(email, new LexicalAnalyzerName("email_index")));
            var expected = Enumerable.Range(3, email.Length - 2).Select(length => email[..length].ToLowerInvariant());
            Assert.That(indexed.Value.Select(token => token.Token), Is.EquivalentTo(expected));
            var searched = await client.AnalyzeTextAsync(name, new AnalyzeTextOptions(email, new LexicalAnalyzerName("email_search")));
            Assert.That(searched.Value.Select(token => token.Token), Is.EqualTo(new[] { email.ToLowerInvariant() }));
            var standard = await client.AnalyzeTextAsync(name, new AnalyzeTextOptions(email, LexicalAnalyzerName.StandardLucene));
            Assert.That(standard.Value.Select(token => token.Token), Is.Not.EqualTo(new[] { email.ToLowerInvariant() }));
        }
        finally
        {
            await client.DeleteIndexAsync(name);
        }
    }
}
