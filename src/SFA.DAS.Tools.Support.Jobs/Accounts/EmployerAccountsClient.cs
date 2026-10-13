using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Configuration;

namespace SFA.DAS.Tools.Support.Jobs.Accounts;

public interface IEmployerAccountsClient
{
    Task<AccountsPage> GetAccounts(int pageNumber, int pageSize, CancellationToken cancellationToken);
    Task<AccountDetail> GetAccount(long accountId, CancellationToken cancellationToken);
    Task<LegalEntity> GetLegalEntity(long accountId, long legalEntityId, CancellationToken cancellationToken);
}

public sealed record AccountSummary([property: JsonRequired] long AccountId);
public sealed record AccountsPage(
    [property: JsonRequired] List<AccountSummary>? Data,
    [property: JsonRequired] int Page,
    [property: JsonRequired] int TotalPages);
public sealed record AccountResource([property: JsonRequired] string? Id);
public sealed record AccountDetail(
    [property: JsonRequired] long AccountId,
    [property: JsonRequired] string? PublicHashedAccountId,
    [property: JsonRequired] string? DasAccountName,
    [property: JsonRequired] List<AccountResource>? PayeSchemes,
    [property: JsonRequired] List<AccountResource>? LegalEntities);
public sealed record LegalEntity([property: JsonRequired] long LegalEntityId, [property: JsonRequired] string? Name);

public sealed class EmployerAccountsClient(HttpClient client, TokenCredential credential, IOptions<JobsOptions> options)
    : IEmployerAccountsClient
{
    public Task<AccountsPage> GetAccounts(int pageNumber, int pageSize, CancellationToken cancellationToken) =>
        Get<AccountsPage>($"api/accounts?pageSize={pageSize}&pageNumber={pageNumber}", cancellationToken);

    public Task<AccountDetail> GetAccount(long accountId, CancellationToken cancellationToken) =>
        Get<AccountDetail>($"api/accounts/{accountId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public Task<LegalEntity> GetLegalEntity(long accountId, long legalEntityId, CancellationToken cancellationToken) =>
        Get<LegalEntity>($"api/accounts/{accountId.ToString(CultureInfo.InvariantCulture)}/legalentities/{legalEntityId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    private async Task<T> Get<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Version", "1");
        var identifierUri = options.Value.EmployerAccountsApiIdentifierUri.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(identifierUri))
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([$"{identifierUri}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Accounts API returned an empty response.");
    }
}
