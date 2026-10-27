using System.Text.Json.Serialization;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record AccountsPage(
    [property: JsonRequired] List<AccountSummary>? Data,
    [property: JsonRequired] int Page,
    [property: JsonRequired] int TotalPages);
