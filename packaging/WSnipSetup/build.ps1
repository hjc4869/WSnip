[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string[]] $Architecture = @('x64', 'arm64'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Version,

    # Light Player checkout that provides the shared sources and build/Ffmpeg.targets; defaults to ..\LightPlayer.
    [string] $LightPlayerRoot
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$desktopProject = Join-Path $repositoryRoot 'src\WSnip.Desktop\WSnip.Desktop.csproj'
$packageProject = Join-Path $PSScriptRoot 'WSnipPackage.wixproj'
$bundleProject = Join-Path $PSScriptRoot 'WSnipBundle.wixproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
    $Version = [string] ($buildProperties.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}

try {
    $parsedVersion = [version] $Version
}
catch {
    throw "Installer version '$Version' is not a valid numeric version.", $_.Exception
}

$major = $parsedVersion.Major
$minor = $parsedVersion.Minor
$patch = [Math]::Max(0, $parsedVersion.Build)
$revision = [Math]::Max(0, $parsedVersion.Revision)
$msiBuild = ($patch * 1000) + $revision
if ($major -gt 255 -or $minor -gt 255 -or $revision -gt 999 -or $msiBuild -gt 65535) {
    throw "Installer version '$Version' cannot be represented safely as a Windows Installer version."
}

$installerVersion = "$major.$minor.$patch.$revision"
$msiVersion = "$major.$minor.$msiBuild"

$sharedProperties = @()
if (-not [string]::IsNullOrWhiteSpace($LightPlayerRoot)) {
    $resolvedLightPlayer = (Resolve-Path $LightPlayerRoot).Path.TrimEnd('\') + '\'
    $sharedProperties += "-p:LightPlayerRoot=$resolvedLightPlayer"
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) exited with code $LASTEXITCODE."
    }
}

function ConvertTo-RtfText {
    param([string] $Text)

    $builder = [System.Text.StringBuilder]::new($Text.Length)
    foreach ($character in $Text.Replace("`r`n", "`n").Replace("`r", "`n").ToCharArray()) {
        $codeUnit = [int] $character
        if ($character -eq '\') {
            [void] $builder.Append('\\')
        }
        elseif ($character -eq '{') {
            [void] $builder.Append('\{')
        }
        elseif ($character -eq '}') {
            [void] $builder.Append('\}')
        }
        elseif ($character -eq "`n") {
            [void] $builder.Append('\par ')
        }
        elseif ($character -eq "`t") {
            [void] $builder.Append('\tab ')
        }
        elseif ($codeUnit -ge 0x20 -and $codeUnit -le 0x7e) {
            [void] $builder.Append($character)
        }
        elseif ($codeUnit -ge 0x20) {
            $signedCodeUnit = if ($codeUnit -gt 0x7fff) { $codeUnit - 0x10000 } else { $codeUnit }
            [void] $builder.Append("\u${signedCodeUnit}?")
        }
    }

    $builder.ToString()
}

$licenseText = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'LICENSE'))
$thirdPartyText = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'THIRDPARTY.txt'))
$bundleLicenseDirectory = Join-Path $repositoryRoot 'artifacts\obj\WSnipSetup\license'
$bundleLicensePath = Join-Path $bundleLicenseDirectory 'LicenseAndThirdParty.rtf'
$bundleLicenseRtf = '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}\viewkind4\uc1\pard\f0\fs18 ' +
    (ConvertTo-RtfText ($licenseText + "`r`n`r`n" + $thirdPartyText)) + '}'
New-Item -ItemType Directory -Path $bundleLicenseDirectory -Force | Out-Null
[System.IO.File]::WriteAllText($bundleLicensePath, $bundleLicenseRtf, [System.Text.UTF8Encoding]::new($false))

foreach ($currentArchitecture in $Architecture) {
    $runtimeIdentifier = "win-$currentArchitecture"
    $msbuildPlatform = if ($currentArchitecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    $architectureRoot = Join-Path $repositoryRoot "artifacts\windows-installer\$currentArchitecture"
    $dotnetArtifacts = Join-Path $architectureRoot 'dotnet'
    $publishDirectory = Join-Path $architectureRoot 'publish'
    $msiDirectory = Join-Path $architectureRoot 'msi'
    $msiPath = Join-Path $msiDirectory "WSnip-$currentArchitecture.msi"
    $setupPath = Join-Path $repositoryRoot "artifacts\release\WSnipSetup-$currentArchitecture.exe"

    Remove-Item -LiteralPath $architectureRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    # NativeAOT, self-contained; the desktop head bundles the LGPL FFmpeg DLLs next to WSnip.exe.
    Invoke-DotNet -Arguments (@(
        'publish', $desktopProject,
        '--configuration', $Configuration,
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '--artifacts-path', $dotnetArtifacts,
        "-p:Platform=$msbuildPlatform",
        "-p:Version=$installerVersion"
    ) + $sharedProperties)

    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'WSnip.exe'))) {
        throw "The $currentArchitecture publish did not produce WSnip.exe."
    }

    Invoke-DotNet -Arguments @(
        'build', $packageProject,
        '--configuration', $Configuration,
        "-p:InstallerArchitecture=$currentArchitecture",
        "-p:MsiVersion=$msiVersion",
        "-p:PublishDirectory=$publishDirectory"
    )

    if (-not (Test-Path -LiteralPath $msiPath)) {
        throw "The $currentArchitecture package build did not produce $msiPath."
    }

    Invoke-DotNet -Arguments @(
        'build', $bundleProject,
        '--configuration', $Configuration,
        "-p:InstallerArchitecture=$currentArchitecture",
        "-p:InstallerVersion=$installerVersion",
        "-p:MsiPath=$msiPath",
        "-p:LicensePath=$bundleLicensePath"
    )

    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "The $currentArchitecture bundle build did not produce $setupPath."
    }

    Write-Host "Created $setupPath"
}
