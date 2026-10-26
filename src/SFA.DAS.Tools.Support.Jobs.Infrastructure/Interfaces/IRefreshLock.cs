namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IRefreshLock
{
    Task<IRefreshLease> Acquire(CancellationToken cancellationToken);
}
