[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageVersion,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $packageRoot -Filter "CRTech.LocaleSmith.McpHost.*.nupkg" -File)
if ($packages.Count -ne 1)
{
    throw "Expected exactly one LocaleSmith MCP Host package, found $($packages.Count)."
}

if (Test-Path -LiteralPath $InstallDirectory)
{
    throw "The isolated tool install directory already exists: $InstallDirectory"
}

New-Item -ItemType Directory -Path $InstallDirectory | Out-Null

$escapedPackageRoot = [System.Security.SecurityElement]::Escape($packageRoot)
$nugetConfigPath = Join-Path $InstallDirectory "NuGet.Config"
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-package" value="$escapedPackageRoot" />
  </packageSources>
</configuration>
"@
[System.IO.File]::WriteAllText(
    $nugetConfigPath,
    $nugetConfig,
    [System.Text.UTF8Encoding]::new($false))

dotnet tool install `
    --tool-path $InstallDirectory `
    --version $PackageVersion `
    --configfile $nugetConfigPath `
    --no-cache `
    CRTech.LocaleSmith.McpHost
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet tool install failed with exit code $LASTEXITCODE."
}

$launcherCandidates = @(
    (Join-Path $InstallDirectory "localesmith-mcp.cmd"),
    (Join-Path $InstallDirectory "localesmith-mcp.exe"),
    (Join-Path $InstallDirectory "localesmith-mcp")
)
$launcher = $launcherCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $launcher)
{
    throw "The installed package did not create a localesmith-mcp launcher."
}

$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"package-smoke","version":"1.0"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
)
$output = @($requests | & $launcher)
if ($LASTEXITCODE -ne 0)
{
    throw "The installed MCP tool exited with code $LASTEXITCODE."
}

$responses = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    $_ | ConvertFrom-Json -Depth 100
})
if ($responses.Count -ne 2)
{
    throw "Expected two MCP responses, received $($responses.Count)."
}

$initialize = $responses | Where-Object { $_.id -eq 1 } | Select-Object -First 1
$toolList = $responses | Where-Object { $_.id -eq 2 } | Select-Object -First 1
if (-not $initialize -or -not $toolList)
{
    throw "The MCP smoke test did not receive initialize and tools/list responses."
}

if ($initialize.result.serverInfo.version -ne $PackageVersion)
{
    throw "MCP server version '$($initialize.result.serverInfo.version)' does not match package version '$PackageVersion'."
}

$toolNames = @($toolList.result.tools | ForEach-Object { $_.name } | Sort-Object)
$expectedToolNames = @("cli.propose", "system.context")
if (Compare-Object -ReferenceObject $expectedToolNames -DifferenceObject $toolNames)
{
    throw "Unexpected MCP tool catalog: $($toolNames -join ', ')"
}

Write-Output "Validated $($packages[0].Name), server version $PackageVersion, and MCP tools: $($toolNames -join ', ')."
