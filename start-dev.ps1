# Launch Beacon locally against the Postgres 18 container on :5432.
# Secrets come from the gitignored .env in this folder.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:Path = "C:\Program Files\nodejs;$env:APPDATA\npm;" + $env:Path

Get-Content (Join-Path $root ".env") | ForEach-Object {
  if ($_ -match '^\s*#' -or $_ -notmatch '=') { return }
  $k, $v = $_.Split('=', 2)
  Set-Item -Path "Env:$k" -Value $v
}

if (-not (docker ps --filter name=beacon-postgres --format "{{.Names}}")) {
  Write-Host "Starting Postgres 18..."
  docker start beacon-postgres | Out-Null
}

Write-Host "API  http://127.0.0.1:8080/health"
Write-Host "Web  http://localhost:3000"
Write-Host "Bootstrap token is BOOTSTRAP_ADMIN_TOKEN in .env"
Write-Host "First user: open /bootstrap  (or this script already created admin / beacon-dev-password if the API is up)"
Write-Host ""
Write-Host "Starting API and web. Ctrl+C stops this window only; close the other jobs from Task Manager if needed."

Start-Process -FilePath "pnpm" -ArgumentList "--filter","@beacon/api","dev" -WorkingDirectory $root
Start-Sleep -Seconds 2
Start-Process -FilePath "pnpm" -ArgumentList "--filter","@beacon/web","dev" -WorkingDirectory $root
