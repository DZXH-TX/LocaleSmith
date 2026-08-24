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

$auditDirectory = Join-Path $packageRoot (".mcp-package-audit-" + [Guid]::NewGuid().ToString("N"))
try
{
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packages[0].FullName, $auditDirectory)
    $nuspecFiles = @(Get-ChildItem -LiteralPath $auditDirectory -Filter "*.nuspec" -File)
    if ($nuspecFiles.Count -ne 1)
    {
        throw "Expected one nuspec in the MCP package, found $($nuspecFiles.Count)."
    }

    [xml]$nuspec = Get-Content -Raw -LiteralPath $nuspecFiles[0].FullName
    $metadata = $nuspec.package.metadata
    if ($metadata.id -cne "CRTech.LocaleSmith.McpHost" -or $metadata.version -cne $PackageVersion)
    {
        throw "Unexpected package identity/version: $($metadata.id) $($metadata.version)."
    }

    if ($metadata.packageTypes.packageType.name -cne "DotnetTool" -or
        $metadata.license.type -cne "expression" -or
        $metadata.license.'#text' -cne "Apache-2.0" -or
        $metadata.readme -cne "README.md" -or
        $metadata.icon -cne "icon.png" -or
        $metadata.repository.url -cne "https://github.com/DZXH-TX/LocaleSmith")
    {
        throw "The MCP package metadata contract is incomplete or unexpected."
    }

    foreach ($requiredPath in @(
        "README.md",
        "icon.png",
        "tools\net10.0\win-x64\DotnetToolSettings.xml",
        "tools\net10.0\win-x64\LocaleSmith.Core.dll",
        "tools\net10.0\win-x64\LocaleSmith.Infrastructure.dll",
        "tools\net10.0\win-x64\LocaleSmith.Mcp.dll",
        "tools\net10.0\win-x64\LocaleSmith.McpHost.dll",
        "tools\net10.0\win-x64\LocaleSmith.McpHost.exe"))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $auditDirectory $requiredPath)))
        {
            throw "MCP package is missing required payload: $requiredPath"
        }
    }

    [xml]$toolSettings = Get-Content -Raw -LiteralPath (
        Join-Path $auditDirectory "tools\net10.0\win-x64\DotnetToolSettings.xml")
    $command = $toolSettings.DotNetCliTool.Commands.Command
    if ($command.Name -cne "localesmith-mcp" -or
        $command.EntryPoint -cne "LocaleSmith.McpHost.exe" -or
        $command.Runner -cne "executable")
    {
        throw "Unexpected DotnetToolSettings command contract."
    }

    foreach ($forbiddenName in @(
        "LocaleSmith.App.dll",
        "LocaleSmith.Archive.dll",
        "LocaleSmith.Presentation.dll",
        "localesmith_core.dll"))
    {
        if (Get-ChildItem -LiteralPath $auditDirectory -Recurse -File -Filter $forbiddenName)
        {
            throw "Standalone MCP package unexpectedly contains $forbiddenName."
        }
    }
}
finally
{
    if (Test-Path -LiteralPath $auditDirectory)
    {
        Remove-Item -LiteralPath $auditDirectory -Recurse -Force
    }
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
