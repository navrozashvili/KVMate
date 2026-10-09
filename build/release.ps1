<#
.SYNOPSIS
    Builds a KVMate release zip.

.DESCRIPTION
    Publishes KVMate.App self-contained for win-x64 and zips the output, with the app files at the
    zip root, as KVMate-<Version>-win-x64.zip. The release workflow runs this; it runs the same way
    locally. Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    The version to stamp, major.minor.patch. Defaults to 0.0.0, which marks a local build.

.PARAMETER OutputDirectory
    Where the zip goes, with the publish output under OutputDirectory\app. Emptied first. Defaults
    to artifacts\release in the repository.

.EXAMPLE
    ./build/release.ps1 -Version 1.0.42
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be major.minor.patch, not '$Version'."
}

$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo 'artifacts\release'
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}

$app = Join-Path $OutputDirectory 'app'

# No -r: the project already sets RuntimeIdentifier=win-x64, and a -r here would become a global
# property that reaches Core's locked restore and fails it.
& dotnet publish (Join-Path $repo 'src\KVMate.App\KVMate.App.csproj') `
    -c Release `
    -p:SelfContained=true `
    -p:RestoreLockedMode=true `
    -p:ContinuousIntegrationBuild=true `
    "-p:Version=$Version" `
    -o $app `
    -nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$zip = Join-Path $OutputDirectory "KVMate-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip

Write-Host "Release $Version is $zip ($((Get-Item $zip).Length.ToString('N0')) bytes)"
