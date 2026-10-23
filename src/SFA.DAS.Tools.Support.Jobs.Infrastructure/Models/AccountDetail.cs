using System.Text.Json.Serialization;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record AccountDetail(
    [property: JsonRequired] long AccountId,
    [property: JsonRequired] string? PublicHashedAccountId,
    [property: JsonRequired] string? DasAccountName,
    [property: JsonRequired] List<AccountResource>? PayeSchemes,
    [property: JsonRequired] List<AccountResource>? LegalEntities);
