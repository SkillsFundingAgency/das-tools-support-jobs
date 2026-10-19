using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SFA.DAS.Tools.Support.Jobs.Infrastructure.Configuration;

namespace SFA.DAS.Tools.Support.Jobs.Infrastructure.Extensions;

public static class AddConfigurationOptionsExtension
{
    public static IServiceCollection AddConfigurationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobsOptions>().Bind(configuration.GetSection(JobsOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.AzureSearchBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https", "AzureSearchBaseUrl must be HTTPS.")
            .Validate(o => Uri.TryCreate(o.EmployerProfilesApiBaseUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" || (uri.IsLoopback && uri.Scheme == "http")), "EmployerProfilesApiBaseUrl must be HTTPS or loopback HTTP.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.EmployerProfilesApiIdentifierUri) ||
                configuration["EnvironmentName"] is "LOCAL" or "DEV", "EmployerProfilesApiIdentifierUri is required outside LOCAL/DEV.")
            .Validate(o => o.PageSize is >= 1 and <= 1000, "PageSize must be between 1 and 1000.")
            .Validate(o => o.VerificationAttempts is >= 1 and <= 120 && o.VerificationDelaySeconds is >= 1 and <= 60,
                "VerificationAttempts and VerificationDelaySeconds must be within supported bounds.")
            .ValidateOnStart();
        return services;
    }
}
