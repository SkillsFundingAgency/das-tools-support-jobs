namespace SFA.DAS.Tools.Support.Jobs.Configuration;

public sealed class JobsOptions
{
    public const string SectionName = "ToolsSupportJobs";
    public string AzureSearchBaseUrl { get; set; } = "";
    public string EmployerProfilesApiBaseUrl { get; set; } = "";
    public string EmployerProfilesApiIdentifierUri { get; set; } = "";
    public int PageSize { get; set; } = 1000;
    public int VerificationAttempts { get; set; } = 12;
    public int VerificationDelaySeconds { get; set; } = 5;
}
