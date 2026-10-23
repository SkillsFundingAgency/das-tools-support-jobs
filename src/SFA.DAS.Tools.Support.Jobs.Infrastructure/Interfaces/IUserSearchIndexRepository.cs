using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

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
