# DAS Tools Support Jobs

Azure Functions backend for [APPMAN-2137](https://skillsfundingagency.atlassian.net/browse/APPMAN-2137). It fully refreshes the support-console user index from `das-employer-profiles-api`, using the shared Azure AI Search service already used by reservations.

## Behaviour

| Function | Trigger | Result |
| --- | --- | --- |
| `RefreshUserSearchIndex` | Daily timer, default 02:00 UTC | Queues a full refresh |
| `RefreshUserSearchIndexHttp` | Function-key protected POST `/api/user-search-index/refresh` | Queues a full refresh and returns HTTP 202 |
| `RefreshUserSearchIndexWorker` | Storage queue `sfa-das-tools-support-user-index-refresh` | Builds, verifies and promotes the new index |

The worker obtains a renewable blob lease, creates `tools-support-users-{yyyyMMddHHmmss}` in UTC and reads every profiles page, starting at page 1 with 1,000 users per page. The Search key and `UserId` are the profile's GUID. The API's `Email` maps to `EmailAddress`; `DisplayName` is searchable and sortable. No email addresses or display names are written to application logs.

Every page must have consistent metadata and the expected number of records; IDs must be valid and unique across the refresh. Every upload must succeed, and the Search document count must match the source `TotalCount`. Count checks allow for indexing delay (12 attempts, five seconds apart by default). Source changes that affect paging cause a failed refresh instead of promoting an incomplete index. Deploy [profiles API PR #318](https://github.com/SkillsFundingAgency/das-employer-profiles-api/pull/318) to order pages by user ID. Offset paging has no snapshot, so a full refresh is not a point-in-time copy of concurrently changing profiles.

Only after verification does alias `tools-support-users` move to the new index, with an ETag condition to detect concurrent alias changes. The actual previous alias target is retained. Older dated user indexes are deleted after promotion. Thus a successful refresh leaves current plus previous (one index on the first run); there can temporarily be a third index while building. Unrelated indexes are untouched.

Upload or verification failures propagate to the Functions queue retry mechanism. The alias stays unchanged, and the failed index is removed where possible. An ambiguous alias update is re-read before deleting anything. A crash, lost lease or cleanup failure can leave a non-serving index for cleanup on the next successful refresh. Monitor the `-poison` queue after five failed attempts. Failed cleanup after promotion also fails the queue invocation, but the verified new index continues serving.

Email fields use `email_index` (`uax_url_email`, lowercase, edge n-grams 3–255) and `email_search` (`uax_url_email`, lowercase). Display name uses the standard analyser. Search consumers query the alias; the UI/query integration belongs to the separate support-console story. Full Lucene fuzzy/prefix queries must be escaped and lower-cased appropriately; Azure does not analyse fuzzy or wildcard query terms.

## Local development

Requires .NET 10 SDK, Azure Functions Core Tools v4, Azurite, access to a development Azure Search service, and a local/development profiles API.

1. Copy `src/SFA.DAS.Tools.Support.Jobs/local.settings.example.json` to `local.settings.json` in the same directory and set the development endpoints. The real settings file is ignored by Git.
2. Start Azurite and the profiles API. Sign in with `az login`; `DefaultAzureCredential` is used for Search and authenticated profiles API calls.
3. Run `dotnet test src/SFA.DAS.Tools.Support.Jobs.sln --configuration Release`.
4. Run `func start` from `src/SFA.DAS.Tools.Support.Jobs`.
5. POST to `http://localhost:7071/api/user-search-index/refresh`. Local Core Tools does not enforce function-key authentication by default.

On Windows, use a short checkout path. If the Functions packaging step exceeds Windows path limits, add `--artifacts-path "$env:TEMP/tsj-build"` to the dotnet commands.

## Configuration and deployment

Settings are Function App environment settings supplied by `azure/template.json`; a separate configuration-table deployment is not required. The `__` separator maps into .NET configuration sections.

| App setting | Purpose |
| --- | --- |
| `AzureWebJobsStorage` | Storage connection for Functions, refresh queue and distributed lease |
| `EnvironmentName` | Environment; unauthenticated profiles calls permitted only in LOCAL/DEV |
| `RefreshUserSearchIndexSchedule` | NCRONTAB schedule; `0 0 2 * * *` by default, UTC |
| `ToolsSupportJobs__AzureSearchBaseUrl` | Shared Search endpoint, derived from `SharedAiSearchName` during deployment |
| `ToolsSupportJobs__EmployerProfilesApiBaseUrl` | Profiles API root URL, before `/api/users` |
| `ToolsSupportJobs__EmployerProfilesApiIdentifierUri` | Entra API resource identifier; tokens request its `/.default` scope |
| `ToolsSupportJobs__PageSize` | Optional, 1–1000; defaults to 1000 |
| `ToolsSupportJobs__VerificationAttempts` | Optional, 1–120; defaults to 12 |
| `ToolsSupportJobs__VerificationDelaySeconds` | Optional, 1–60; defaults to 5 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Existing Application Insights connection string |

Create an Azure DevOps pipeline using `azure-pipelines.yml`. CI builds/tests/publishes without deployment variable groups. To deploy, manually select `DeployEnvironment` (AT, TEST, TEST2 or DEMO); its default is `none`. Normal environment approvals remain in effect. The build produces `SFA.DAS.Tools.Support.Jobs.zip` and the Azure deployment files in the `SFA.DAS.Tools.Support.Jobs` artifact.

Deployment uses `DevTest Management Resources`, `<ENV> DevTest Shared Resources` and `<ENV> das-tools-support-jobs`. Supply these variables across those groups:

- `SubscriptionId`, `ResourceEnvironmentName`, `EnvironmentName`, `ResourceGroupLocation`, `SharedEnvResourceGroup`, `SharedAiSearchName` (the existing reservations Search service).
- `SubnetResourceId` for an existing App Service delegated subnet with routes/DNS permitting profiles, Search and storage access; `WorkerAccessRestrictions` as a JSON array.
- `SharedStorageAccountConnectionString` and `ApplicationInsightsConnectionString` as secret variables.
- `EmployerProfilesApiBaseUrl`, `EmployerProfilesApiIdentifierUri`, and `Tags` as a JSON object.

The template creates a Windows S1 App Service plan, Function App and system-assigned managed identity, with .NET 10 isolated worker, Always On and VNet integration. It assigns the platform's existing `AiSearchIndexContributor` role at the shared Search service scope, following reservations. The deployment identity needs subscription/RG deployment permissions and permission to assign that role. No new Search service is created.

Before running the first refresh, DevOps must grant the Function App managed identity the profiles API's `Default` application role. The template outputs its principal ID. This Entra app-role assignment is external to ARM and is not automatically granted here. Confirm API access and network connectivity before enabling/testing the daily schedule. Search must allow Entra/RBAC data-plane authentication. The app uses the shared storage connection, so no storage RBAC assignment is required.

See [tester instructions](docs/testing.md) for acceptance checks and the optional live analyser test.
