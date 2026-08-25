# Install local Beacon agent settings. Paste a project token when the console asks.
# The token is hidden and is never written into Grok / Cursor / Claude / opencode configs.
# Double-click setup.cmd (not this file) so the window stays open.

param(
  [switch]$NoPause,
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$Remaining = @()
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:Path = "C:\Program Files\nodejs;$env:APPDATA\npm;" + $env:Path

function Get-FlagValue {
  param(
    [string[]]$All,
    [string]$Name
  )
  for ($i = 0; $i -lt $All.Count; $i++) {
    $arg = $All[$i]
    if ($arg -eq $Name -and ($i + 1) -lt $All.Count) {
      return $All[$i + 1]
    }
    $prefix = "$Name="
    if ($arg.StartsWith($prefix)) {
      return $arg.Substring($prefix.Length)
    }
  }
  return $null
}

function Remove-Flag {
  param(
    [string[]]$All,
    [string]$Name
  )
  $next = @()
  for ($i = 0; $i -lt $All.Count; $i++) {
    $arg = $All[$i]
    if ($arg -eq $Name) {
      $i++
      continue
    }
    if ($arg.StartsWith("$Name=")) {
      continue
    }
    $next += $arg
  }
  return $next
}

function Read-Secret {
  param([string]$Prompt)
  $secure = Read-Host -Prompt $Prompt -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try {
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  } finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
  }
}

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
  param([int]$Code)
  if ($NoPause -or $env:CI -or $env:BEACON_SETUP_PAUSE -eq "0") {
    return
  }
  $fromExplorer = Test-LaunchedFromExplorer
  if ($env:BEACON_SETUP_PAUSE -eq "1" -or $fromExplorer) {
    Write-Host ""
    Write-Host "Press Enter to close..."
    try {
      [void][Console]::ReadLine()
    } catch {
      Read-Host | Out-Null
    }
  }
}

$forward = @()
if ($Remaining) {
  $forward = @($Remaining)
}
$code = 1
try {
  if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    throw "pnpm is required. Install Node.js 22+ and pnpm, then re-run setup.cmd."
  }

  Write-Host "ProjectBeacon setup"
  Write-Host "Mint a project token in Agents, then paste it when asked. Input is hidden."
  Write-Host "Setup writes BEACON_HOME and local MCP snippets. It does not mint tokens or start a hosted agent."
  Write-Host ""

  $token = Get-FlagValue -All $forward -Name "--token"
  $project = Get-FlagValue -All $forward -Name "--project"
  $url = Get-FlagValue -All $forward -Name "--url"
  $forward = @(Remove-Flag -All $forward -Name "--token")
  $forward = @(Remove-Flag -All $forward -Name "--project")
  $forward = @(Remove-Flag -All $forward -Name "--url")

  if (-not $token) {
    $token = (Read-Secret -Prompt "Paste project token (hidden, starts with bcn_)").Trim()
    if (-not $token) {
      throw "A project token is required. Mint one in Agents, then paste it here."
    }
  }
  if (-not $project) {
    $project = (Read-Host "Project id").Trim()
    if (-not $project) {
      throw "A project id is required."
    }
  }
  if (-not $url) {
    $defaultUrl = if ($env:BEACON_URL) { $env:BEACON_URL } else { "http://127.0.0.1:8080" }
    $url = (Read-Host "Control plane URL [$defaultUrl]").Trim()
    if (-not $url) {
      $url = $defaultUrl
    }
  }

  if (-not (Get-FlagValue -All $forward -Name "--cwd")) {
    $forward += @("--cwd", $root)
  }

  $env:BEACON_SETUP_TOKEN = $token
  $env:BEACON_SETUP_PROJECT = $project
  $env:BEACON_SETUP_URL = $url
  try {
    & pnpm --filter "@beacon/cli" start -- setup @forward
    $code = $LASTEXITCODE
  } finally {
    Remove-Item Env:BEACON_SETUP_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:BEACON_SETUP_PROJECT -ErrorAction SilentlyContinue
    Remove-Item Env:BEACON_SETUP_URL -ErrorAction SilentlyContinue
  }
  if ($code -ne 0) {
    throw "beacon setup exited with code $code. Scroll up for the Beacon error above the pnpm line."
  }
} catch {
  $code = 1
  Write-Host $_.Exception.Message -ForegroundColor Red
}

Pause-IfNeeded -Code $code
exit $code
