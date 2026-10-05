# APPMAN-2137 testing

## Before testing

Merge/build the supporting configuration PR, deploy Profiles API PR #319, and deploy this Function App to a development/test environment using the shared pipeline and variables in the README. Confirm the configuration-table and app-role assignment steps succeeded. Use its existing shared Search service. Check that the profiles API supports `GET /api/users?pageSize=1000&pageNumber=1` and returns `userProfiles`, `totalCount`, `pageNumber`, `pageSize`. Its profile fields are `id`, `email`, `displayName`.

For failure injection, point a test instance of the Function App at a controlled profiles API stub containing synthetic users. Do not change source user records or shared-service permissions to manufacture errors. Avoid running unrelated refreshes against the same user alias during a test.

## Success and retention

1. Record the current `tools-support-users` alias target, dated user index names and source `TotalCount`.
2. POST `/api/user-search-index/refresh` on the Function App with its function key in the `x-functions-key` header. Expect 202; this means queued, not completed.
3. Follow the `RefreshUserSearchIndexWorker` invocation in Application Insights. Look for page numbers, page/upload counts, verification and `User search alias switched`, then `User search refresh completed`. Check exceptions and the refresh poison queue if completion is absent.
4. Confirm the new name is `tools-support-users-{yyyyMMddHHmmss}` and its document count equals the API `TotalCount`. Check a known user's `Id`, `UserId`, `EmailAddress` and `DisplayName` against the source. An empty source must produce a verified empty index.
5. If the starting alias was A, confirm it now points at B and A still exists. Run another successful refresh (at least one second later); confirm C serves, B remains, A is deleted, and exactly two dated user indexes remain. Other indexes, especially reservations, must remain untouched.
6. Confirm the timer is configured for `0 0 2 * * *` (UTC). Invoke `RefreshUserSearchIndex` through the Functions admin/Test-Run interface in the test environment, or observe a scheduled execution; it must queue the same worker flow.

## Failed refresh and concurrency

With a healthy A serving, use the stub to return HTTP 500 on a later page. Confirm the worker fails, A stays serving, and the candidate is deleted where possible. Repeat with a short page, duplicate user ID, wrong page number or changed total count. The log must not show an alias switch for that candidate.

Run the unit suite to verify failed batch results, count mismatch, delayed count convergence, alias-update failures and cancellation on lease loss. It explicitly checks that a remotely successful alias change with a lost response never causes the serving index to be deleted. These cases use fakes rather than damaging the shared Search service.

Send two manual requests while a large refresh is running. Both may return 202, but the shared blob lease must permit only one worker to mutate indexes at a time. The other invocation can retry. Check final alias health, counts and the current/previous retention rules after both finish. If work is repeatedly failing, inspect `<refresh-queue>-poison`; 202 does not guarantee eventual success.

## Email analyser and search

Use Search's Analyze API on a dated user index, with test text `User.Name+Test@Example.com`. With analyser `email_index`, expect lower-cased prefixes `use`, `user`, `user.`, …, `user.name+test@example.com`. With `email_search`, expect the single whole lower-cased email. Compare with `standard.lucene`, which splits this patterned value. The n-gram range is 3–255; terms shorter than three characters are not indexed as email prefixes.

An opt-in integration test creates an isolated temporary analyser-test index, calls the actual Azure Analyze API, then removes that temporary index. It neither uploads users nor changes the serving alias. Set `TOOLS_SUPPORT_TEST_SEARCH_URL` to a development Search endpoint, sign in with a permitted account, and run:

```powershell
dotnet test src/SFA.DAS.Tools.Support.Jobs.sln --configuration Release --filter FullyQualifiedName~LiveEmailAnalyzerTests
```

For a known indexed synthetic user, query **the alias** using the email prefix (for example `user.name`) with `searchFields=EmailAddress`. For display names use `queryType=full`, `searchFields=DisplayName` and `search=tes*` for a `Test User` record. For fuzzy matching try `search=tset~1` on `DisplayName`. Escape Lucene special characters in user-supplied full-syntax queries; lower-case prefix/fuzzy email terms. This story provides the index; it does not replace the support UI search client.

Keep evidence of each alias target, retained indexes, source/index count, worker invocation outcome and analyser output. The automated suite and a successful publish validate code; permissions, deployment and real Search behaviour require these environment checks.

## Acceptance status before Azure testing

The automated suite covers the implementation; it does not sign off the Azure environment.

| Story criterion | Code/local evidence | Required live evidence |
| --- | --- | --- |
| AC1: daily refresh builds a dated index from all pages, verifies then switches alias | Timer/queue tests, multi-page refresh, count and upload validation | Scheduled invocation, all-page logs, matching source/index counts and alias target |
| AC2: refresh failure leaves alias unchanged | API/page/upload failure tests | Controlled later-page failure with A still serving |
| AC3: successful B retains A | Retention test | Alias B; A and B both present |
| AC4: successful C deletes A and keeps B | Three-generation retention test | Alias C; only B and C remain |
| AC5: verification failure cannot promote | Count mismatch, incomplete upload and changed-source tests | Failed verification with unchanged serving alias |
| AC6: email analyser preserves whole email and emits lower-case prefixes from length 3 | Schema tests; explicit live Analyze API test supplied | Real Analyze API output for `user.name+test@example.com` |

Record the deployed commit, successful pipeline run, worker invocation IDs, alias/index counts and analyser output before marking APPMAN-2137 complete.
