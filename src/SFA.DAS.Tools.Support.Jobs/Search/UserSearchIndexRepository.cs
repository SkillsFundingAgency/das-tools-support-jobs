using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

namespace SFA.DAS.Tools.Support.Jobs.Search;

public sealed record IndexAlias(string? Target, SearchAlias? Definition = null);

public interface IUserSearchIndexRepository
{
    Task<IndexAlias> GetAlias(CancellationToken cancellationToken);
    Task Create(string name, CancellationToken cancellationToken);
    Task Upload(string name, IReadOnlyCollection<UserSearchDocument> documents, CancellationToken cancellationToken);
    Task<long> Count(string name, CancellationToken cancellationToken);
    Task Promote(string name, IndexAlias previous, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> List(CancellationToken cancellationToken);
    Task Delete(string name, CancellationToken cancellationToken);
}

public sealed class UserSearchIndexRepository(SearchIndexClient client) : IUserSearchIndexRepository
{
    public const string AliasName = "tools-support-users";

    public async Task<IndexAlias> GetAlias(CancellationToken cancellationToken)
    {
        try
        {
            var alias = (await client.GetAliasAsync(AliasName, cancellationToken)).Value;
            return new IndexAlias(alias.Indexes.Single(), alias);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new IndexAlias(null);
        }
    }

    public async Task Create(string name, CancellationToken cancellationToken) =>
        await client.CreateIndexAsync(UserSearchDocument.CreateIndex(name), cancellationToken);

    public async Task Upload(string name, IReadOnlyCollection<UserSearchDocument> documents, CancellationToken cancellationToken)
    {
        var result = await client.GetSearchClient(name).MergeOrUploadDocumentsAsync(documents,
            new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken);
        if (result.Value.Results.Count != documents.Count || result.Value.Results.Any(result => !result.Succeeded))
        {
            throw new InvalidOperationException("Search did not successfully upload every document in the batch.");
        }
    }

    public async Task<long> Count(string name, CancellationToken cancellationToken) =>
        (await client.GetSearchClient(name).GetDocumentCountAsync(cancellationToken)).Value;

    public async Task Promote(string name, IndexAlias previous, CancellationToken cancellationToken)
    {
        var alias = previous.Definition ?? new SearchAlias(AliasName, name);
        alias.Indexes.Clear();
        alias.Indexes.Add(name);
        await client.CreateOrUpdateAliasAsync(AliasName, alias, onlyIfUnchanged: true, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<string>> List(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var name in client.GetIndexNamesAsync(cancellationToken))
        {
            names.Add(name);
        }
        return names;
    }

    public async Task Delete(string name, CancellationToken cancellationToken) =>
        await client.DeleteIndexAsync(name, cancellationToken: cancellationToken);
}
