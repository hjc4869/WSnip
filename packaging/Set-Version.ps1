[CmdletBinding()]
param(
    # A release tag such as v1.2.3 or v1.2.3.4. Without one, the version in Directory.Build.props is kept.
    [string] $Tag
)

# Stamps one four-part version into Directory.Build.props (assemblies, WiX setup) and the MSIX
# manifest, so every package of a release carries the same version. Prints the version.
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$manifestPath = Join-Path $repositoryRoot 'packaging\WSnipMSIX\Package.appxmanifest'

[string] $props = [IO.File]::ReadAllText($propsPath)
if ($props -notmatch '<Version>([^<]+)</Version>') {
    throw "Directory.Build.props has no <Version>."
}
$version = [version] $Matches[1]

if ($Tag -match '^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
    $revision = if ($Matches[4]) { [int] $Matches[4] } else { 0 }
    $version = [version]::new([int] $Matches[1], [int] $Matches[2], [int] $Matches[3], $revision)
}
elseif (-not [string]::IsNullOrWhiteSpace($Tag)) {
    Write-Warning "Tag '$Tag' is not a numeric version; keeping $version from Directory.Build.props."
}

$text = "$($version.Major).$($version.Minor).$([Math]::Max(0, $version.Build)).$([Math]::Max(0, $version.Revision))"

# Text replacements keep both files' formatting intact.
$props = [regex]::Replace($props, '<Version>[^<]+</Version>', "<Version>$text</Version>")
[IO.File]::WriteAllText($propsPath, $props, [Text.UTF8Encoding]::new($false))

[string] $manifest = [IO.File]::ReadAllText($manifestPath)
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*?\sVersion=")[^"]*(")', "`${1}$text`${2}")
[IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))

$text
