[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $HostDirectory
)

$ErrorActionPreference = 'Stop'
$cliDirectory = Join-Path (Resolve-Path -LiteralPath $HostDirectory).Path 'CLI'
$requiredFiles = @(
    'bettergi.exe',
    'BetterGenshinImpact.Cli.dll',
    'BetterGenshinImpact.Cli.deps.json',
    'BetterGenshinImpact.Cli.runtimeconfig.json',
    'BetterGenshinImpact.Automation.dll',
    'Newtonsoft.Json.dll'
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $cliDirectory $file) -PathType Leaf)) {
        throw "Bundled CLI file is missing: $file"
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $cliDirectory 'BetterGenshinImpact.Cli.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or
    $runtimeConfig.runtimeOptions.framework.version -notlike '8.*') {
    throw 'Bundled CLI must use the installed .NET 8 runtime.'
}

$schemaText = & (Join-Path $cliDirectory 'bettergi.exe') schema
if ($LASTEXITCODE -ne 0) {
    throw "Bundled CLI schema failed with exit code $LASTEXITCODE."
}
$schema = ($schemaText -join [Environment]::NewLine) | ConvertFrom-Json
if (-not $schema.ok -or $schema.exitCode -ne 0 -or $schema.apiVersion -ne 1 -or
    $schema.data.name -ne 'bettergi' -or $schema.data.commands -notcontains 'doctor') {
    throw 'Bundled CLI schema does not match the automation contract.'
}
Write-Output "Bundled CLI verified: $cliDirectory"
