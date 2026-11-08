<#
.SYNOPSIS
    Run Revit Test Console tests inside a real Revit process, non-interactively.
.DESCRIPTION
    Drives the Revit-in-process autorun mode via REVITTESTCONSOLE_TESTRUNNER_*
    environment variables (TestRunnerApplication fires TestRunnerCommand.RunHeadless
    on the first Idling event — no dialogs), polls for the <Output>.done sentinel,
    parses the result XML, then terminates the Revit process.
.EXAMPLE
    .\run-revit-autorun.ps1 -RevitVersion 2024 -Include "RevitIntegration" -Output "D:\work\results.xml"
.EXAMPLE
    .\run-revit-autorun.ps1 -RevitExe "C:\Program Files\Autodesk\Revit 2026\Revit.exe" -Output ".\out\ci.xml"
.NOTES
    Exit codes: 0 = all tests passed; 1 = test failures; 2 = infrastructure error
    (Revit missing/crashed, error sentinel, result XML unreadable); 3 = overall timeout.
    Prereq: RevitTestConsole.Core/TestRunnerCommand must be registered as an add-in
    in the target Revit (see README "Revit 进程内运行" section).
#>
param(
    # Revit major version, used for default install-dir discovery (C:/D: drive, Program Files).
    [string]$RevitVersion = "2024",
    # Full path to Revit.exe. Overrides -RevitVersion discovery when given.
    [string]$RevitExe = "",
    [string]$Include = "",
    [string]$Exclude = "",
    [string]$Output = "",
    # Optional directory to copy result XML + autorun log into (evidence archiving).
    [string]$Evidence = "",
    # Optional document to open at Revit startup (relative paths resolve against the script directory).
    [string]$StartupDocument = "",
    # Per-test timeout forwarded to REVITTESTCONSOLE_TESTRUNNER_TIMEOUT (0 = inline execution on the API-context thread).
    [int]$PerTestTimeoutMs = 0,
    [int]$OverallTimeoutMinutes = 30,
    [int]$PollSeconds = 5
)

$ErrorActionPreference = "Stop"

# ----------------------------------------------------------------------------
# Resolve Revit.exe
# ----------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($RevitExe))
{
    $candidates = @(
        "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe",
        "D:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"
    )
    $RevitExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($RevitExe) -or -not (Test-Path $RevitExe))
{
    Write-Error "Revit.exe not found. Pass -RevitExe <path> or install Revit $RevitVersion to a default location."
    exit 2
}
$revitDir = Split-Path -Parent $RevitExe
Write-Host "[AUTORUN-DRIVER] Revit: $RevitExe"

# ----------------------------------------------------------------------------
# Resolve output path (default: .\RevitTestConsole-TestResults-<timestamp>.xml)
# ----------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Output))
{
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $Output = Join-Path $PSScriptRoot "..\out\RevitTestConsole-TestResults-$stamp.xml"
}
$Output = [System.IO.Path]::GetFullPath($Output)
$outputDir = Split-Path -Parent $Output
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
$donePath = "$Output.done"
$errorPath = "$Output.error"
$logPath = "$Output.log"
Remove-Item -Force -ErrorAction SilentlyContinue $donePath, $errorPath

# ----------------------------------------------------------------------------
# Clean stale Revit processes from prior runs (journal lock blocks a fresh start)
# ----------------------------------------------------------------------------
$stale = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($stale)
{
    Write-Host "[AUTORUN-DRIVER] WARNING: $($stale.Count) existing Revit process(es) detected; a parallel instance may fail on the journal lock."
}

# ----------------------------------------------------------------------------
# Arm autorun environment (read by TestRunnerApplication/RunHeadless inside Revit)
# ----------------------------------------------------------------------------
$env:REVITTESTCONSOLE_TESTRUNNER_AUTORUN = "1"
$env:REVITTESTCONSOLE_TESTRUNNER_INCLUDE = $Include
$env:REVITTESTCONSOLE_TESTRUNNER_EXCLUDE = $Exclude
$env:REVITTESTCONSOLE_TESTRUNNER_OUTPUT = $Output
$env:REVITTESTCONSOLE_TESTRUNNER_TIMEOUT = "$PerTestTimeoutMs"
Write-Host "[AUTORUN-DRIVER] include=[$Include] exclude=[$Exclude] output=[$Output] perTestTimeoutMs=$PerTestTimeoutMs"

# ----------------------------------------------------------------------------
# Launch Revit
# ----------------------------------------------------------------------------
$revitArgs = @()
if (-not [string]::IsNullOrWhiteSpace($StartupDocument))
{
    $docPath = $StartupDocument
    if (-not [System.IO.Path]::IsPathRooted($docPath)) { $docPath = Join-Path $PSScriptRoot $docPath }
    if (Test-Path $docPath) { $revitArgs += $docPath }
    else { Write-Warning "Startup document not found: $docPath (starting Revit empty)" }
}

Write-Host "[AUTORUN-DRIVER] Starting Revit..."
$revitProc = Start-Process -FilePath $RevitExe -ArgumentList $revitArgs -WorkingDirectory $revitDir -PassThru
Write-Host "[AUTORUN-DRIVER] Revit PID: $($revitProc.Id)"

# ----------------------------------------------------------------------------
# Poll for sentinel / error / timeout
# ----------------------------------------------------------------------------
$deadline = (Get-Date).AddMinutes($OverallTimeoutMinutes)
$result = 2
$reason = ""
while ($true)
{
    if (Test-Path $errorPath)
    {
        $reason = "error sentinel present: " + (Get-Content $errorPath -Raw)
        $result = 2
        break
    }
    if (Test-Path $donePath)
    {
        $result = 0
        break
    }
    if ($revitProc.HasExited)
    {
        $reason = "Revit exited (code $($revitProc.ExitCode)) before writing the .done sentinel — likely a startup crash or add-in load failure. See journal and $logPath"
        $result = 2
        break
    }
    if ((Get-Date) -gt $deadline)
    {
        $reason = "overall timeout after $OverallTimeoutMinutes min — Revit did not finish; forcing termination"
        $result = 3
        break
    }
    Start-Sleep -Seconds $PollSeconds
}

# ----------------------------------------------------------------------------
# Terminate Revit (autorun mode intentionally keeps Revit alive; the driver owns its lifecycle)
# ----------------------------------------------------------------------------
if (-not $revitProc.HasExited)
{
    Write-Host "[AUTORUN-DRIVER] Stopping Revit (PID $($revitProc.Id))..."
    Stop-Process -Id $revitProc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

if ($result -ne 0)
{
    Write-Error "[AUTORUN-DRIVER] FAILED ($result): $reason"
    if (Test-Path $logPath) { Write-Host "[AUTORUN-DRIVER] Last autorun log lines:"; Get-Content $logPath -Tail 30 | ForEach-Object { Write-Host "  $_" } }
    exit $result
}

# ----------------------------------------------------------------------------
# Parse result XML
# ----------------------------------------------------------------------------
if (-not (Test-Path $Output))
{
    Write-Error "[AUTORUN-DRIVER] FAILED (2): .done sentinel present but result XML missing: $Output"
    exit 2
}
[xml]$xml = Get-Content $Output -Raw
$node = $xml.SelectSingleNode("//*[local-name()='test-run']")
if ($node -eq $null) { $node = $xml.DocumentElement }
$total = [int]$node.total; $passed = [int]$node.passed; $failed = [int]$node.failed
$inconclusive = [int]$node.inconclusive; $skipped = [int]$node.skipped
Write-Host "[AUTORUN-DRIVER] total=$total passed=$passed failed=$failed inconclusive=$inconclusive skipped=$skipped"

if (-not [string]::IsNullOrWhiteSpace($Evidence))
{
    if (-not (Test-Path $Evidence)) { New-Item -ItemType Directory -Path $Evidence -Force | Out-Null }
    Copy-Item $Output -Destination $Evidence -Force
    if (Test-Path $logPath) { Copy-Item $logPath -Destination $Evidence -Force }
    Write-Host "[AUTORUN-DRIVER] Evidence copied to $Evidence"
}

if ($failed -gt 0) { exit 1 }
exit 0
