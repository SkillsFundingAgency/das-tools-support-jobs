using Microsoft.Extensions.DependencyInjection;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Interfaces;
using SFA.DAS.Tools.Support.Jobs.Services;

namespace SFA.DAS.Tools.Support.Jobs.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRefreshServices(this IServiceCollection services)
    {
        services.AddTransient<IUserSearchIndexRefresh, UserSearchIndexRefresh>();
        services.AddTransient<IAccountSearchIndexRefresh, AccountSearchIndexRefresh>();
        return services;
    }
}
