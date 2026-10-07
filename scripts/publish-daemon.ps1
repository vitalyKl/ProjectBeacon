# Publishes the beacon workstation client (self-contained single file) for all
# desktop RIDs into artifacts\daemon-<rid>\ and zips each as beacon-<rid>-<version>.zip.
#
# Usage:
#   pwsh scripts\publish-daemon.ps1                 # all desktop RIDs
#   pwsh scripts\publish-daemon.ps1 -Rid win-x64   # one RID

param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repo 'ProjectBeacon.Cli\ProjectBeacon.Cli.csproj'
$artifacts = Join-Path $repo 'artifacts'

$version = (Select-String -Path (Join-Path $repo 'Directory.Build.props') -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value

foreach ($r in $Rid) {
    $out = Join-Path $artifacts "daemon-$r"
    Write-Host "==> publishing daemon for $r"
    dotnet publish $cliProject `
        -c Release -r $r --self-contained true `
        -p:PublishSingleFile=true `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $r" }

    $zip = Join-Path $artifacts "beacon-$r-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "==> $zip"
}
Write-Host "done."
