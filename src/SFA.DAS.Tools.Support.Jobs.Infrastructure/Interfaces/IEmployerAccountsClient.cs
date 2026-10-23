using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IEmployerAccountsClient
{
    Task<AccountsPage> GetAccounts(int pageNumber, int pageSize, CancellationToken cancellationToken);
    Task<AccountDetail> GetAccount(long accountId, CancellationToken cancellationToken);
    Task<LegalEntity> GetLegalEntity(long accountId, long legalEntityId, CancellationToken cancellationToken);
}
