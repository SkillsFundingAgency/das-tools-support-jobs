using SFA.DAS.Tools.Support.Jobs.Infrastructure.Models;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IEmployerProfilesClient
{
    Task<UserProfilesPage> GetUsers(int pageNumber, int pageSize, CancellationToken cancellationToken);
}
