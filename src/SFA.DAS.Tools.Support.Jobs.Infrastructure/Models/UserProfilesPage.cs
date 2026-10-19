using System.Text.Json.Serialization;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record UserProfilesPage(
    [property: JsonRequired] List<UserProfile>? UserProfiles,
    [property: JsonRequired] int TotalCount,
    [property: JsonRequired] int PageNumber,
    [property: JsonRequired] int PageSize);
