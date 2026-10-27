using System.Text.Json.Serialization;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record LegalEntity([property: JsonRequired] long LegalEntityId, [property: JsonRequired] string? Name);
