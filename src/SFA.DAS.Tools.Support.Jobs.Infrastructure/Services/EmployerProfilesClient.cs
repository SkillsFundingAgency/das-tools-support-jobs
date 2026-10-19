using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Services;

public sealed class EmployerProfilesClient(HttpClient client, TokenCredential credential, IOptions<JobsOptions> options)
    : IEmployerProfilesClient
{
    public async Task<UserProfilesPage> GetUsers(int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/users?pageSize={pageSize}&pageNumber={pageNumber}");
        request.Headers.Add("X-Version", "1");
        var identifierUri = options.Value.EmployerProfilesApiIdentifierUri.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(identifierUri))
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([$"{identifierUri}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfilesPage>(cancellationToken)
            ?? throw new InvalidOperationException("Profiles API returned an empty response.");
    }
}
