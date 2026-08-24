[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedAppPublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedMcpPublishDirectory,

    [string]$ExpectedIdentityName = "CRTech.LocaleSmith.Dev",

    [string]$ExpectedPublisher = "CN=LocaleSmith Development",

    [string]$ExpectedVersion = "1.2.0.0"
)

$ErrorActionPreference = "Stop"

function Get-SingleFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required MSIX payload file is missing: $RelativePath"
    }

    return Get-Item -LiteralPath $path
}

function Assert-EqualFileHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPath,

        [Parameter(Mandatory = $true)]
        [string]$ActualPath
    )

    $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExpectedPath).Hash
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ActualPath).Hash
    if (-not $expectedHash.Equals($actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Stale MSIX payload detected: $(Split-Path -Leaf $ActualPath) does not match the current publish input."
    }
}

function Test-IsProjectOwnedFile {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileInfo]$File
    )

    return $File.Name -eq "localesmith_core.dll" -or
        ($File.Name.StartsWith("LocaleSmith.", [StringComparison]::OrdinalIgnoreCase) -and
         ($File.Extension -in @(".dll", ".exe") -or
          $File.Name.EndsWith(".deps.json", [StringComparison]::OrdinalIgnoreCase) -or
          $File.Name.EndsWith(".runtimeconfig.json", [StringComparison]::OrdinalIgnoreCase)))
}

function Assert-CurrentPublishPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishRoot,

        [Parameter(Mandatory = $true)]
        [string]$PayloadRoot
    )

    $expectedFiles = @(
        Get-ChildItem -LiteralPath $PublishRoot -Recurse -File |
            Where-Object { Test-IsProjectOwnedFile $_ }
    )
    if ($expectedFiles.Count -eq 0) {
        throw "No LocaleSmith publish files were found under '$PublishRoot'."
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($expectedFile in $expectedFiles) {
        $relativePath = [IO.Path]::GetRelativePath($PublishRoot, $expectedFile.FullName)
        $null = $expectedPaths.Add($relativePath)
        $actualPath = Join-Path $PayloadRoot $relativePath
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf)) {
            throw "Current publish file is missing from the MSIX payload: $relativePath"
        }

        Assert-EqualFileHash $expectedFile.FullName $actualPath
    }

    $actualFiles = @(
        Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File |
            Where-Object { Test-IsProjectOwnedFile $_ }
    )
    foreach ($actualFile in $actualFiles) {
        $relativePath = [IO.Path]::GetRelativePath($PayloadRoot, $actualFile.FullName)
        if (-not $expectedPaths.Contains($relativePath)) {
            throw "Unexpected stale LocaleSmith file is present in the MSIX payload: $relativePath"
        }
    }
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$resolvedAppPublish = (Resolve-Path -LiteralPath $ExpectedAppPublishDirectory).Path
$resolvedMcpPublish = (Resolve-Path -LiteralPath $ExpectedMcpPublishDirectory).Path
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$extractDirectory = Join-Path $temporaryRoot ("LocaleSmith-msix-audit-" + [Guid]::NewGuid().ToString("N"))

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($resolvedPackage, $extractDirectory)

    $manifestPath = Join-Path $extractDirectory "AppxManifest.xml"
    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $namespaceManager = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace("appx", $manifest.DocumentElement.NamespaceURI)
    $identity = $manifest.SelectSingleNode("/appx:Package/appx:Identity", $namespaceManager)
    if ($null -eq $identity) {
        throw "The unpacked MSIX does not contain one package Identity."
    }

    $identityName = $identity.GetAttribute("Name")
    $identityVersion = $identity.GetAttribute("Version")
    $identityPublisher = $identity.GetAttribute("Publisher")
    $identityArchitecture = $identity.GetAttribute("ProcessorArchitecture")
    if ($identityName -ne $ExpectedIdentityName) {
        throw "Unexpected MSIX identity '$identityName'; expected '$ExpectedIdentityName'."
    }

    if ($identityVersion -ne $ExpectedVersion) {
        throw "Unexpected MSIX version '$identityVersion'; expected '$ExpectedVersion'."
    }

    if ($identityPublisher -ne $ExpectedPublisher) {
        throw "Unexpected MSIX publisher '$identityPublisher'; expected '$ExpectedPublisher'."
    }

    if ($identityArchitecture -ne "x64") {
        throw "Unexpected MSIX architecture '$identityArchitecture'; expected 'x64'."
    }

    $resourcesPri = Get-SingleFile $extractDirectory "resources.pri"
    if ($resourcesPri.Length -lt 1024) {
        throw "The package resources.pri is unexpectedly small."
    }

    $makePri = Get-ChildItem `
        -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makepri.exe" `
        -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $makePri) {
        throw "makepri.exe was not found; package XAML resources cannot be audited."
    }

    $priDumpPath = Join-Path $extractDirectory "resources.pri.xml"
    & $makePri.FullName dump /if $resourcesPri.FullName /of $priDumpPath /dt detailed /o | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "makepri.exe failed to dump package resources with exit code $LASTEXITCODE."
    }

    [xml]$priDump = Get-Content -Raw -LiteralPath $priDumpPath
    $xbfNames = @($priDump.SelectNodes("//*[local-name()='NamedResource']") | ForEach-Object {
        $_.GetAttribute("name")
    })
    $requiredXbfNames = @(
        "App.xbf",
        "MainWindow.xbf",
        "CliConfirmationDialog.xbf",
        "AssistantPage.xbf",
        "CommunityPage.xbf",
        "DashboardPage.xbf",
        "LogsPage.xbf",
        "ModelSourcesPage.xbf",
        "OnboardingPage.xbf",
        "SettingsPage.xbf",
        "Controls.xbf",
        "MicrosoftStoreBillingControl.xbf",
        "ModArtifactDownloadControl.xbf"
    )
    foreach ($xbfName in $requiredXbfNames) {
        if ($xbfName -notin $xbfNames) {
            throw "Package resources.pri does not contain required XAML resource '$xbfName'."
        }
    }

    if (Test-Path -LiteralPath (Join-Path $extractDirectory "AppxSignature.p7x")) {
        throw "The validation package must remain unsigned, but AppxSignature.p7x is present."
    }

    $packageSignature = Get-AuthenticodeSignature -LiteralPath $resolvedPackage
    if ($packageSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "The validation package must be unsigned; signature status is '$($packageSignature.Status)'."
    }
    $appExe = Get-SingleFile $extractDirectory "LocaleSmith.App\LocaleSmith.App.exe"
    $appDll = Get-SingleFile $extractDirectory "LocaleSmith.App\LocaleSmith.App.dll"
    $presentationDll = Get-SingleFile $extractDirectory "LocaleSmith.App\LocaleSmith.Presentation.dll"
    $nativeDll = Get-SingleFile $extractDirectory "LocaleSmith.App\localesmith_core.dll"
    $mcpExe = Get-SingleFile $extractDirectory "LocaleSmith.McpHost\LocaleSmith.McpHost.exe"
    $mcpDll = Get-SingleFile $extractDirectory "LocaleSmith.McpHost\LocaleSmith.McpHost.dll"
    foreach ($asset in @(
        "Assets\Square44x44Logo.png",
        "Assets\Square150x150Logo.png",
        "Assets\StoreLogo.png",
        "Assets\Wide310x150Logo.png")) {
        $null = Get-SingleFile $extractDirectory $asset
    }

    Assert-CurrentPublishPayload $resolvedAppPublish (Join-Path $extractDirectory "LocaleSmith.App")
    Assert-CurrentPublishPayload $resolvedMcpPublish (Join-Path $extractDirectory "LocaleSmith.McpHost")

    $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe.FullName).FileVersion
    if ($appVersion -ne $ExpectedVersion) {
        throw "Unexpected App file version '$appVersion'; expected '$ExpectedVersion'."
    }

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $extractDirectory -Recurse -File | Where-Object {
        $_.Extension -in @(".pfx", ".p12", ".pem", ".key", ".cer")
    })
    if ($forbiddenFiles.Count -ne 0) {
        throw "Signing or key material must not enter the MSIX payload: $($forbiddenFiles.FullName -join ', ')"
    }

    $packageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedPackage).Hash
    Write-Output "MSIX audit passed."
    Write-Output "Identity=$identityName"
    Write-Output "Version=$identityVersion"
    Write-Output "PackageBytes=$((Get-Item -LiteralPath $resolvedPackage).Length)"
    Write-Output "PackageSHA256=$packageHash"
    Write-Output "ResourcesPriBytes=$($resourcesPri.Length)"
    Write-Output "AppSHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $appDll.FullName).Hash)"
    Write-Output "McpHostSHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $mcpExe.FullName).Hash)"
}
finally {
    $resolvedExtractDirectory = [IO.Path]::GetFullPath($extractDirectory)
    if ($resolvedExtractDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedExtractDirectory)) {
        Remove-Item -LiteralPath $resolvedExtractDirectory -Recurse -Force
    }
}
