namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;

public interface IRefreshRequestQueue
{
    Task Enqueue(CancellationToken cancellationToken);
}
