$ErrorActionPreference = 'Stop'
$required = @('SubscriptionId', 'ResourceEnvironmentName', 'EnvironmentName', 'ResourceGroupLocation',
    'SharedEnvResourceGroup', 'SharedAiSearchName', 'SubnetResourceId', 'WorkerAccessRestrictions',
    'SharedStorageAccountConnectionString', 'ApplicationInsightsConnectionString',
    'EmployerProfilesApiBaseUrl', 'EmployerProfilesApiIdentifierUri', 'Tags')
$missing = @($required | Where-Object {
    $value = [Environment]::GetEnvironmentVariable($_)
    [string]::IsNullOrWhiteSpace($value) -or $value -match '\$\([^)]+\)'
})
if ($missing.Count) { throw "Missing or unresolved deployment variables: $($missing -join ', ')" }

$parameters = @{}
foreach ($key in $required | Where-Object { $_ -ne 'SubscriptionId' }) {
    $name = $key.Substring(0, 1).ToLowerInvariant() + $key.Substring(1)
    $value = [Environment]::GetEnvironmentVariable($key)
    if ($key -in @('WorkerAccessRestrictions', 'Tags')) { $value = ConvertFrom-Json -InputObject $value -NoEnumerate }
    $parameters[$name] = @{ value = $value }
}
$parameterFile = Join-Path ([IO.Path]::GetTempPath()) ("tools-support-jobs-" + [guid]::NewGuid() + '.json')
try {
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $parameters } |
        ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $parameterFile -Encoding utf8
    $result = az deployment sub create --subscription $env:SubscriptionId --name "tools-support-jobs-$env:BUILD_BUILDID" `
        --location $env:ResourceGroupLocation --template-file "$PSScriptRoot/template.json" --parameters "@$parameterFile" `
        --query properties.outputs --output json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Function App infrastructure deployment failed.' }
    $outputs = ($result -join "`n") | ConvertFrom-Json
    Write-Host "##vso[task.setvariable variable=FunctionAppName]$($outputs.FunctionAppName.value)"
    Write-Host "Function App deployed: $($outputs.FunctionAppName.value)"
    Write-Host "Managed identity principal: $($outputs.ManagedIdentityPrincipalId.value)"
}
finally {
    if (Test-Path -LiteralPath $parameterFile) { Remove-Item -LiteralPath $parameterFile }
}
