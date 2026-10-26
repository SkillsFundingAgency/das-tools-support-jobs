namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IUserSearchIndexRefresh
{
    Task Run(CancellationToken cancellationToken);
}
