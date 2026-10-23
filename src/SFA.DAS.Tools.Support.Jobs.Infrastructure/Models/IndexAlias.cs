using Azure.Search.Documents.Indexes.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

public sealed record IndexAlias(string? Target, SearchAlias? Definition = null);
