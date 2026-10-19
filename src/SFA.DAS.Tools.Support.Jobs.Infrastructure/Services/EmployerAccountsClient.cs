using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

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
