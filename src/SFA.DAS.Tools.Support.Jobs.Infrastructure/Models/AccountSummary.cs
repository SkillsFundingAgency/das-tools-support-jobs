using System.Text.Json.Serialization;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record AccountSummary([property: JsonRequired] long AccountId);
