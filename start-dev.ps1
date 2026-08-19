# Prefer start.cmd (double-click). This file now forwards to start.ps1.
& "$PSScriptRoot\start.ps1" @args
exit $LASTEXITCODE
