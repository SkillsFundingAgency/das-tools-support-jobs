namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IAccountSearchIndexRefresh
{
    Task Run(CancellationToken cancellationToken);
}
