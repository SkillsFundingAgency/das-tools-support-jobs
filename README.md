# DAS Tools Support Jobs

Azure Functions backend for [APPMAN-2137](https://skillsfundingagency.atlassian.net/browse/APPMAN-2137) and [APPMAN-2138](https://skillsfundingagency.atlassian.net/browse/APPMAN-2138). It fully refreshes the support-console user index from `das-employer-profiles-api`, using the shared Azure Search service already used by reservations.

## Behaviour

| Function | Trigger | Result |
| --- | --- | --- |
| `RefreshUserSearchIndex` | Daily timer, default 02:00 UTC | Queues a full refresh |
| `RefreshUserSearchIndexHttp` | Function-key protected POST `/api/user-search-index/refresh` | Queues a full refresh and returns HTTP 202 |
| `RefreshUserSearchIndexWorker` | Storage queue `sfa-das-tools-support-user-index-refresh` | Builds, verifies and promotes the new index |

The worker obtains a renewable blob lease, creates `tools-support-users-{yyyyMMddHHmmss}` in UTC and reads every profiles page, starting at page 1 with 1,000 users per page. The Search key and `UserId` are the profile's GUID. The API's `Email` maps to `EmailAddress`; `DisplayName` is searchable and sortable. No email addresses or display names are written to application logs.

Every page must have consistent metadata and the expected number of records; IDs must be valid and unique across the refresh. Every upload must succeed, and the Search document count must match the source `TotalCount`. Count checks allow for indexing delay (12 attempts, five seconds apart by default). Source changes that affect paging cause a failed refresh instead of promoting an incomplete index. Deploy the accompanying Employer Profiles API paging change to order pages by user ID. Offset paging has no snapshot, so a full refresh is not a point-in-time copy of concurrently changing profiles.

Only after verification does alias `tools-support-users` move to the new index, with an ETag condition to detect concurrent alias changes. The actual previous alias target is retained. Older dated user indexes are deleted after promotion. Thus a successful refresh leaves current plus previous (one index on the first run); there can temporarily be a third index while building. Unrelated indexes are untouched.

Upload or verification failures propagate to the Functions queue retry mechanism. The alias stays unchanged, and the failed index is removed where possible. An ambiguous alias update is re-read before deleting anything. A crash, lost lease or cleanup failure can leave a non-serving index for cleanup on the next successful refresh. Monitor the `-poison` queue after five failed attempts. Failed cleanup after promotion also fails the queue invocation, but the verified new index continues serving.

Email fields use `email_index` (`uax_url_email`, lowercase, edge n-grams 3–255) and `email_search` (`uax_url_email`, lowercase). Display name uses the standard analyser. Search consumers query the alias; the UI/query integration belongs to the separate support-console story. Full Lucene fuzzy/prefix queries must be escaped and lower-cased appropriately; Azure does not analyse fuzzy or wildcard query terms.

## Solution structure

The project names and separation follow `das-employer-finance-jobs`:

| Project | Responsibility |
| --- | --- |
| `SFA.DAS.Tools.Support.Jobs.Functions` | Function host and separate timer, HTTP and queue trigger classes |
| `SFA.DAS.Tools.Support.Jobs.Core` | User and account search refresh workflows and their service registration |
| `SFA.DAS.Tools.Support.Jobs.Infrastructure` | Configuration, interfaces, models, API client, Search repository, queue and blob lease |
| `SFA.DAS.Tools.Support.Jobs.UnitTests` | Tests grouped by configuration, functions, services and opt-in integration tests |

The Functions project references Core and Infrastructure; Core references Infrastructure. The configuration-table name, Function App resource names, function names, HTTP route, queue and Search alias are unchanged by the project separation.

## Local development

Requires .NET 10 SDK, Azure Functions Core Tools v4, Azurite, access to a development Azure Search service, and a local/development profiles API.

1. Copy `src/SFA.DAS.Tools.Support.Jobs.Functions/local.settings.example.json` to `local.settings.json` in the same directory and set the development endpoints. The real settings file is ignored by Git.
2. Start Azurite and the profiles API. Sign in with `az login`; `DefaultAzureCredential` is used for Search and authenticated profiles API calls.
3. Run `dotnet test src/SFA.DAS.Tools.Support.Jobs.sln --configuration Release`.
4. Run `func start` from `src/SFA.DAS.Tools.Support.Jobs.Functions`.
5. POST to `http://localhost:7071/api/user-search-index/refresh`. Local Core Tools does not enforce function-key authentication by default.

On Windows, use a short checkout path. If the Functions packaging step exceeds Windows path limits, add `--artifacts-path "$env:TEMP/tsj-build"` to the dotnet commands.

## Configuration and deployment

The Azure DevOps pipeline follows `das-employer-finance-jobs`: shared DAS build/deploy templates, GitVersion, SonarCloud, environment variable groups, the `das-employer-config` pipeline artifact, configuration-table generation and managed-identity app-role assignment. Templates are pinned to building-blocks 3.0.14 and platform-automation 5.1.19. The Search role ARM template is pinned to 3.0.17, matching reservations.

The build installs .NET 10, runs unit tests with OpenCover coverage, scans packages, publishes the Function App zip and packages `azure/template.json`. `coverlet.msbuild` supplies the report required by the shared Sonar template. Generated Functions SDK source is excluded from coverage; application code is included. The opt-in live Azure analyser test is excluded from normal CI and must be run separately in a test environment.

Deployment stages are AT, TEST, TEST2, DEMO, PP, PROD and MO, using the same service connections and Azure DevOps environments as finance-jobs. Each depends on Build. AT uses the existing main/manual/PR condition; the other stages use the existing environment approval gates. Configure those approvals before enabling a new pipeline.

Each deployment waits for competing deployments, provisions ARM resources, generates configuration version `1.0` into the `Configuration` table, assigns the Profiles API app role, then deploys the Function App package. Package path within artifact `SFA.DAS.Tools.Support.Jobs` is `SFA.DAS.Tools.Support.Jobs.Functions/SFA.DAS.Tools.Support.Jobs.Functions.zip`.

### Required pipeline setup

1. Merge/build the supporting `das-employer-config` change first. Its **master** artifact must contain `das-tools-support-jobs/SFA.DAS.Tools.Support.Jobs.schema.json` and the `-toolssupjobs-fa` role assignment. The shared app-role step only runs with the config master artifact.
2. Create/authorize the Azure DevOps definition using this repository's `azure-pipelines.yml`; authorize GitHub, shared template repositories, service connections, the DAS GitHub App secure file, config pipeline resource and existing BUILD/RELEASE management groups. Create the SonarCloud project `SkillsFundingAgency_das-tools-support-jobs` and configure its service connection access.
3. Create/authorize `RELEASE das-tools-support-jobs` and the AT/TEST/TEST2/DEMO/PreProd/PROD/MO `das-tools-support-jobs` groups. Keep `ServiceName=toolssupjobs` so resource names match the configured app-role suffix. Each stage also uses its matching management/shared groups, as in finance-jobs.
4. Supply the variables below, confirm the target environment's approval gates, then run Build and deploy to AT first. Use a config artifact from master that contains the new schema/assignment.
5. Deploy the accompanying Employer Profiles API paging change before the first refresh.

| Pipeline variables | Purpose |
| --- | --- |
| `SubscriptionId`, `Tenant`, `ResourceEnvironmentName`, `EnvironmentName`, `ResourceGroupLocation`, `SharedEnvResourceGroup`, `Tags` | Deployment context; `Tags` is a JSON object |
| `SharedAiSearchName`, `AzureSearchBaseUrl` | Existing reservations Search service name and matching HTTPS endpoint |
| `SubnetResourceId`, `WorkerAccessRestrictions` | Existing delegated App Service subnet and JSON access-restriction array |
| `SharedStorageAccountConnectionString` | Secret: Functions queue, trigger state and distributed lease |
| `ConfigurationStorageConnectionString`, `ConfigurationStorageAccountName` | Secret connection string and account name for DAS configuration storage |
| `ApplicationInsightsConnectionString` | Secret: existing Application Insights connection |
| `EmployerProfilesApiBaseUrl` | HTTPS Profiles API root URL, before `/api/users` |
| `EmployerProfilesApiIdentifier` | Existing Entra identifier for the Profiles API; maps to `EmployerProfilesApiIdentifierUri` in application config |
| `RefreshUserSearchIndexSchedule` | Optional NCRONTAB override; default `0 0 2 * * *`, UTC |
| `AppServicePlanSku` | Optional plan SKU override; default S1 |

ARM supplies host settings `AzureWebJobsStorage`, `EnvironmentName`, `RefreshUserSearchIndexSchedule`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `ConfigNames=SFA.DAS.Tools.Support.Jobs_1.0` and `ConfigurationStorageConnectionString`. The timer schedule stays in Function App settings because the Functions host cannot read worker-only table configuration.

Application settings are under `ToolsSupportJobs` in the configuration table: `AzureSearchBaseUrl`, `EmployerProfilesApiBaseUrl`, `EmployerProfilesApiIdentifierUri`, `PageSize` (1000), `VerificationAttempts` (12) and `VerificationDelaySeconds` (5). Environment variables using the `ToolsSupportJobs__` prefix can override these. LOCAL/DEV can use only the environment settings exported by Core Tools without a table connection; deployed environments require the versioned table configuration.

The ARM template creates a Windows S1 plan, .NET 10 isolated Function App, system-assigned managed identity, Always On and VNet integration. It assigns the platform `AiSearchIndexContributor` role at the existing shared Search service scope. The shared app-role step grants the Function App the Profiles API `Default` role using `das-app-role-assignments-CDS` (DevTest) or `das-app-role-assignments-FCS` (PP/PROD/MO). The appropriate deployment identity needs permission to create the resources and assign Search access; the app-role connection needs Entra assignment permissions.

Confirm routes/DNS permit Profiles API, Search, configuration storage and job storage access. Search must allow Entra/RBAC data-plane authentication. Storage uses connection strings. A successful local build does not verify these permissions or network paths.

## Account search index

APPMAN-2138 adds account refreshes to this Function App. It reuses the Search client, managed identity, queue implementation and renewable blob lease. Account refreshes use their own queue and lock blob, so user and account refreshes can run and retry independently. Missing or invalid account-only configuration fails account workers without preventing the existing user refresh from resolving.

| Function | Trigger | Result |
| --- | --- | --- |
| `RefreshAccountSearchIndex` | Daily timer, default 02:30 UTC (`0 30 2 * * *`) | Queues a full account refresh |
| `RefreshAccountSearchIndexHttp` | Function-key protected POST `/api/account-search-index/refresh` | Queues an account refresh and returns HTTP 202 |
| `RefreshAccountSearchIndexWorker` | Storage queue `sfa-das-tools-support-account-index-refresh` | Builds, verifies and promotes the account index |

The worker reads `GET /api/accounts?pageSize=1000&pageNumber={n}` from the Employer Accounts API. Its paging contract is `Data`, `Page`, `TotalPages`, with account IDs in `Data`. The current API reports an extra empty final page when the account count is an exact page-size multiple; the worker reads it. All non-final pages must be full, page metadata must stay consistent, and IDs must be positive and unique. This endpoint does not supply `TotalCount`; verification compares Search's count with the unique accounts read across every reported page. Offset paging is not a snapshot of concurrent source changes.

Each account is enriched from `GET /api/accounts/{accountId}`. `PublicHashedAccountId` maps to `PublicHashedId`, `DasAccountName` to `AccountName`, and PAYE resource IDs to the `PayeSchemes` collection. Legal-entity resource IDs are used with `GET /api/accounts/{accountId}/legalentities/{legalEntityId}` to populate `LegalEntityNames`. Returned links are not followed. Account/relationship mismatches or failed detail calls fail the refresh rather than dropping search fields. Logs contain page and document counts, not account names or PAYE values.

Alias `tools-support-accounts` points to a dated `tools-support-accounts-{yyyyMMddHHmmss}` index. Uploads must all succeed and the document count must converge before promotion. Promotion uses the observed alias ETag; the previous alias target is retained until the next success. Cleanup only removes older dated account indexes. User and reservations indexes remain outside account cleanup. A lost alias-update response is re-read before removing a candidate, so a remotely promoted serving index is preserved.

`Id` is the invariant numeric account ID; `AccountId` is filterable. `PublicHashedId` and `PayeSchemes` are searchable and filterable. Keyword analysers preserve full references, including PAYE slashes, lower-case search tokens, and index prefixes of length 3–255. `AccountName` is searchable/sortable and `LegalEntityNames` is searchable with the standard analyser; full Lucene prefix/fuzzy queries can search name fragments. Consumers must escape full-syntax query input. UI/query integration is a separate story.

### Account deployment configuration

Use the supporting `das-employer-config` schema `das-tools-support-jobs/SFA.DAS.Tools.Support.Jobs.schema.json` from configuration master after it is merged and built. Add pipeline variables `EmployerAccountsApiBaseUrl` (HTTPS Accounts API root before `/api/accounts`) and `EmployerAccountsApiIdentifier` (Accounts API Entra identifier). They populate `ToolsSupportJobs.EmployerAccountsApiBaseUrl` and `EmployerAccountsApiIdentifierUri`; LOCAL/DEV can override them with the corresponding `ToolsSupportJobs__` environment variables from `local.settings.example.json`.

The deployment ARM template supplies `RefreshAccountSearchIndexSchedule`, defaulting to `0 30 2 * * *`. A pipeline variable of the same name can override the default through the existing ARM parameter-generation step. The user schedule remains separately configured.

Before deployment, the Function App managed identity ending `-toolssupjobs-fa` needs `ReadAllEmployerAccountBalances` and `ReadAllAccountUsers` on the Accounts API registration ending `-accapi-as-ar`. The first permits account listing and legal-entity reads; the second permits numeric account detail reads. Retain the APPMAN-2137 Profiles API `Default` assignment on `-empprfapi-as-ar`. Apply the shared app-role assignments only after those permission changes are approved. Confirm the configured identifier points to the matching Accounts API registration and that network rules permit its endpoint. A local test does not prove deployed access.
