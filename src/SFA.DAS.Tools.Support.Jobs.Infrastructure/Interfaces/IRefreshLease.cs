namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IRefreshLease : IAsyncDisposable
{
    CancellationToken CancellationToken { get; }
}
