# Start Beacon locally and keep this window open with live logs.
# Double-click start.cmd. Ctrl+C stops API, web, and worker.

param(
  [switch]$NoPause,
  [switch]$SkipWorker
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:Path = "C:\Program Files\nodejs;$env:APPDATA\npm;" + $env:Path

$apiUrl = "http://127.0.0.1:8080"
$webUrl = "http://127.0.0.1:3000"
$logDir = Join-Path $root ".cache\start"
$children = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$exitCode = 0

function Test-LaunchedFromExplorer {
  try {
    $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId = $PID").ParentProcessId
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $parentId"
    return [bool]($parent.Name -match '^(explorer|sihost)\.exe$')
  } catch {
    return $false
  }
}

function Pause-IfNeeded {
  if ($NoPause -or $env:CI -or $env:BEACON_START_PAUSE -eq "0") {
    return
  }
  if ($env:BEACON_START_PAUSE -eq "1" -or (Test-LaunchedFromExplorer)) {
    Write-Host ""
    Write-Host "Press Enter to close..."
    try {
      [void][Console]::ReadLine()
    } catch {
      Read-Host | Out-Null
    }
  }
}

function Import-DotEnv {
  param([string]$Path)
  if (-not (Test-Path $Path)) {
    throw "Missing $Path. Copy .env.example to .env and set POSTGRES_PASSWORD, BEACON_WORKER_TOKEN, and INDEX_RPC_TOKEN."
  }
  Get-Content $Path | ForEach-Object {
    if ($_ -match '^\s*#' -or $_ -notmatch '=') {
      return
    }
    $name, $value = $_.Split('=', 2)
    $name = $name.Trim()
    if (-not $name) {
      return
    }
    Set-Item -Path "Env:$name" -Value $value
  }
}

function Get-PnpmPath {
  $cmd = Get-Command pnpm.cmd -ErrorAction SilentlyContinue
  if ($cmd) {
    return $cmd.Source
  }
  $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
  if ($pnpm) {
    return $pnpm.Source
  }
  throw "pnpm is required. Install Node.js 22+ and pnpm, then re-run start.cmd."
}

function Stop-PortListeners {
  param([int[]]$Ports)
  $killed = @{}
  foreach ($port in $Ports) {
    $conns = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    foreach ($conn in $conns) {
      $procId = $conn.OwningProcess
      if (-not $procId -or $procId -eq 0 -or $killed.ContainsKey($procId)) {
        continue
      }
      $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
      if (-not $proc) {
        continue
      }
      if ($proc.ProcessName -notmatch '^(node|pnpm)$') {
        Write-Host "Port $port is held by $($proc.ProcessName) (pid $procId); leaving it alone."
        continue
      }
      Write-Host "Stopping leftover $($proc.ProcessName) on port $port (pid $procId)."
      Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
      $killed[$procId] = $true
    }
  }
  if ($killed.Count -gt 0) {
    Start-Sleep -Seconds 1
  }
}

function Test-HttpOk {
  param([string]$Url, [int]$TimeoutSec = 3)
  try {
    $res = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec
    return $res.StatusCode -ge 200 -and $res.StatusCode -lt 500
  } catch {
    return $false
  }
}

function Wait-HttpOk {
  param([string]$Name, [string]$Url, [int]$Seconds = 90)
  $deadline = (Get-Date).AddSeconds($Seconds)
  while ((Get-Date) -lt $deadline) {
    if (Test-HttpOk -Url $Url) {
      Write-Host "$Name is up: $Url"
      return
    }
    Start-Sleep -Seconds 1
  }
  throw "$Name did not become ready at $Url within ${Seconds}s. Scroll the logs above."
}

function Start-LoggedProcess {
  param(
    [string]$Name,
    [string]$FilePath,
    [string[]]$ArgumentList
  )
  if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
  }
  $out = Join-Path $logDir "$Name.out.log"
  $err = Join-Path $logDir "$Name.err.log"
  foreach ($file in @($out, $err)) {
    if (Test-Path $file) {
      Remove-Item $file -Force
    }
  }
  $proc = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $root -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $children.Add($proc) | Out-Null
  return [pscustomobject]@{
    Name = $Name
    Process = $proc
    Out = $out
    Err = $err
    OutOffset = 0
    ErrOffset = 0
  }
}

function Write-NewLogLines {
  param($Tracked)
  foreach ($item in $Tracked) {
    foreach ($pair in @(@{ Path = $item.Out; Kind = "out" }, @{ Path = $item.Err; Kind = "err" })) {
      if (-not (Test-Path $pair.Path)) {
        continue
      }
      $lines = Get-Content $pair.Path -ErrorAction SilentlyContinue
      if (-not $lines) {
        continue
      }
      $count = @($lines).Count
      $offset = if ($pair.Kind -eq "out") { $item.OutOffset } else { $item.ErrOffset }
      if ($count -le $offset) {
        continue
      }
      for ($i = $offset; $i -lt $count; $i++) {
        $line = $lines[$i]
        if ($null -eq $line -or $line -eq "") {
          continue
        }
        Write-Host ("[{0}] {1}" -f $item.Name, $line)
      }
      if ($pair.Kind -eq "out") {
        $item.OutOffset = $count
      } else {
        $item.ErrOffset = $count
      }
    }
  }
}

function Stop-Children {
  foreach ($proc in $children) {
    if (-not $proc -or $proc.HasExited) {
      continue
    }
    try {
      Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    } catch {
    }
  }
}

try {
  Import-DotEnv (Join-Path $root ".env")

  if (-not $env:DATABASE_URL) {
    if (-not $env:POSTGRES_PASSWORD) {
      throw "Set DATABASE_URL or POSTGRES_PASSWORD in .env."
    }
    $env:DATABASE_URL = "postgres://beacon:$($env:POSTGRES_PASSWORD)@127.0.0.1:5432/beacon"
  }

  $env:HOST = "0.0.0.0"
  $env:BEACON_API_URL = $apiUrl
  $env:BEACON_PUBLIC_URL = "http://localhost:3000"
  $env:INDEX_RPC_URL = "http://127.0.0.1:7744"
  $env:INDEX_RPC_HOST = "127.0.0.1"
  $env:INDEX_RPC_PORT = "7744"
  if (-not $env:AUTH_LOCAL) {
    $env:AUTH_LOCAL = "true"
  }
  if (-not $env:BEACON_INDEX_DIR) {
    $env:BEACON_INDEX_DIR = Join-Path $root ".cache\index"
  }
  if (-not $env:BEACON_CLONE_DIR) {
    $env:BEACON_CLONE_DIR = Join-Path $root ".cache\clones"
  }
  if (-not $env:BEACON_WORKSPACE) {
    $env:BEACON_WORKSPACE = $root
  }

  $pnpm = Get-PnpmPath
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker is required to start the beacon-postgres container."
  }

  $pg = docker ps -a --filter name=beacon-postgres --format "{{.Names}} {{.Status}}" 2>&1
  if (-not $pg) {
    throw "Container beacon-postgres was not found. Create it once, or use docker compose up -d postgres."
  }
  if ($pg -notmatch 'Up ') {
    Write-Host "Starting Postgres..."
    docker start beacon-postgres | Out-Null
  }

  $pgReady = $false
  for ($i = 0; $i -lt 30; $i++) {
    docker exec beacon-postgres pg_isready -U beacon -d beacon 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
      $pgReady = $true
      break
    }
    Start-Sleep -Seconds 1
  }
  if (-not $pgReady) {
    throw "Postgres did not become ready."
  }
  Write-Host "Postgres is up on :5432"

  Write-Host "Applying database migrations..."
  & $pnpm --filter "@beacon/db" db:migrate
  if ($LASTEXITCODE -ne 0) {
    throw "Database migrations failed. Reports and other schema-backed screens need a current database."
  }

  Write-Host "Freeing leftover Node listeners on 3000, 3010, 8080, and 7744..."
  Stop-PortListeners -Ports @(3000, 3010, 8080, 7744)

  $lock = Join-Path $root "apps\web\.next\dev\lock"
  if (Test-Path $lock) {
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
  }

  $tracked = @()
  Write-Host "Starting API..."
  $tracked += Start-LoggedProcess -Name "api" -FilePath $pnpm -ArgumentList @("--filter", "@beacon/api", "dev")
  Wait-HttpOk -Name "API" -Url "$apiUrl/health" -Seconds 60

  $startWorker = -not $SkipWorker -and $env:BEACON_WORKER_TOKEN -and $env:INDEX_RPC_TOKEN
  if ($startWorker) {
    Write-Host "Starting worker..."
    $tracked += Start-LoggedProcess -Name "worker" -FilePath $pnpm -ArgumentList @("--filter", "@beacon/worker", "dev")
  } elseif (-not $SkipWorker) {
    Write-Host "Skipping worker: set BEACON_WORKER_TOKEN and INDEX_RPC_TOKEN in .env to start it."
  }

  Write-Host "Starting web..."
  $tracked += Start-LoggedProcess -Name "web" -FilePath $pnpm -ArgumentList @("--filter", "@beacon/web", "dev")
  Wait-HttpOk -Name "Web" -Url $webUrl -Seconds 120

  Write-Host ""
  Write-Host "Beacon is running. This window stays open."
  Write-Host "  Web       $webUrl"
  Write-Host "  Login     $webUrl/login"
  Write-Host "  Bootstrap $webUrl/bootstrap"
  Write-Host "  API       $apiUrl/health"
  Write-Host "Ctrl+C stops every service started by this script."
  Write-Host ""

  try {
    while ($true) {
      Write-NewLogLines $tracked
      foreach ($item in $tracked) {
        if ($item.Process.HasExited) {
          Write-NewLogLines $tracked
          throw "$($item.Name) exited with code $($item.Process.ExitCode)."
        }
      }
      Start-Sleep -Milliseconds 500
    }
  } finally {
    Write-Host "Stopping services..."
    Stop-Children
  }
} catch {
  $exitCode = 1
  Write-Host $_.Exception.Message -ForegroundColor Red
  Stop-Children
}

Pause-IfNeeded
exit $exitCode
