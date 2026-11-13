<#
  AVREG_01 / TESTGATE_01 / TESTCLEAN_01 — docs/README.md#GOV-AVGATE.
  full = A/V + Core/App + verifier + Python + harness checks.
  all = full + standalone media exports + native decoder soak.
  quick = iteration only, never the completion gate.
  TESTOUT_01: every suite writes ONLY to logs\<Suite>_Results\ (console.log, <Suite>.trx, work\).
  A suite's folder is replaced right before that suite runs, so each holds exactly its latest result;
  suites this mode does not run keep their last result. Gate verdict: logs\Gate_Results\LAST_RESULT.txt.
#>
param([ValidateSet('full', 'quick', 'all')][string]$Mode = 'full')
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestHarness.ps1')
$root = Split-Path -Parent $PSScriptRoot
$gateDir = Get-TestSuiteRoot $root 'Gate'
$started = Get-Date
$lease = $null
$oldNative = $env:FVS_TEST_NATIVE_MPV
$oldManaged = $env:FVS_TEST_OUTPUT_MANAGED

function Enter-Suite([string]$name) {
    Reset-TestOutputs $root @($name)
    $suiteDir = Get-TestSuiteRoot $root $name
    $null = New-Item -ItemType Directory -Force -Path $suiteDir
    return $suiteDir
}
function Invoke-Suite([string]$name, [string]$project, [string]$filter, [string[]]$required = @()) {
    $suiteDir = Enter-Suite $name
    $runLog = Join-Path $suiteDir 'console.log'
    $trx = Join-Path $suiteDir "$name.trx"
    $dargs = @('test', (Join-Path $root $project), '-c', 'Release', '--nologo', '-v', 'q',
        '--logger', "trx;LogFileName=$name.trx", '--results-directory', $suiteDir)
    if ($filter) { $dargs += @('--filter', $filter) }
    # Native stderr is diagnostic text, not a PowerShell exception (including under PS 5).
    $ErrorActionPreference = 'Continue'
    & dotnet @dargs *>> $runLog
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Read-TestResults $trx $name $code $required
}
function Invoke-Check([string]$name, [string]$exe, [string[]]$arguments) {
    $runLog = Join-Path (Enter-Suite $name) 'console.log'
    $ErrorActionPreference = 'Continue'
    & $exe @arguments *>> $runLog
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    [pscustomobject]@{ Suite = $name; Name = $name; Outcome = $(if ($code -eq 0) { 'Passed' } else { 'Failed' });
        Message = $(if ($code -eq 0) { '' } else { "Exited $code; see logs\${name}_Results\console.log" }); Where = '' }
}
try {
    Set-Location $root
    $lease = Enter-TestRun $root
    $env:FVS_TEST_OUTPUT_MANAGED = '1'
    $null = Enter-Suite 'Gate'
    $env:FVS_TEST_NATIVE_MPV = '1'
    $all = @(Invoke-Check 'Harness' 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tests/TestHarness.Tests.ps1'))
    $required = if ($Mode -eq 'quick') { @('S01','S02','S03','S04','S05','S06','S07','S08') } else { @(1..38 | ForEach-Object { 'S{0:D2}' -f $_ }) }
    $filter = if ($Mode -eq 'quick') { 'Tier=Quick' } else { '' }
    $all += Invoke-Suite 'AV' 'tests/FreeVideoStudio.AvRegression.Tests/FreeVideoStudio.AvRegression.Tests.csproj' $filter $required
    if ($Mode -ne 'quick') {
        $all += Invoke-Suite 'Core' 'tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj' ''
        $all += Invoke-Suite 'App' 'tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj' ''
        $all += Invoke-Suite 'Verifier' 'tests/FvsVerify.Tests/FvsVerify.Tests.csproj' ''
        $all += Invoke-Check 'Sentinels' 'dotnet' @('run', '--project', 'build/FvsVerify/FvsVerify.csproj', '-c', 'Release', '--no-build')
        $all += Invoke-Check 'Python' 'python' @('-B', '-m', 'unittest', 'discover', '-s', 'tests/DeveloperTools.Tests', '-v')
        $all += Invoke-Check 'DevSandbox' 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tests/DevHarness.Tests/DevSandbox.Tests.ps1')
    }
    if ($Mode -eq 'all') {
        $all += Invoke-Check 'DesktopPreview' 'dotnet' @('run', '--project', 'tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj', '-c', 'Release')
        $all += Invoke-Check 'MergerDesktop' 'dotnet' @('run', '--project', 'tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj', '-c', 'Release', '--no-build', '--', '--merger')
        $all += Invoke-Check 'MergerSilentDesktop' 'dotnet' @('run', '--project', 'tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj', '-c', 'Release', '--no-build', '--', '--merger', '--silent-middle')
        $all += Invoke-Check 'Media' 'dotnet' @('run', '--project', 'tests/MediaPipelineChecks/MediaPipelineChecks.csproj', '-c', 'Release', '--', '--keep-artifacts')
        $all += Invoke-Check 'DecoderSoak' 'dotnet' @('run', '--project', 'tests/MediaPipelineChecks/MediaPipelineChecks.csproj', '-c', 'Release', '--no-build', '--', '--libav-soak', '--keep-artifacts')
    }
    $failed = @($all | Where-Object Outcome -ne 'Passed')
    $passed = @($all | Where-Object Outcome -eq 'Passed').Count
    $skipped = @($all | Where-Object Outcome -eq 'NotExecuted').Count
    $elapsed = (Get-Date) - $started
    $verdict = if ($failed.Count -eq 0 -and $passed -gt 0) { 'PASS' } else { 'FAIL' }
    $summary = @("AV GATE ($Mode): $verdict - $passed passed; $($failed.Count) unsuccessful (including $skipped skipped) in $([int]$elapsed.TotalMinutes)m$($elapsed.Seconds)s")
    foreach ($group in ($all | Group-Object Suite)) {
        $ok = @($group.Group | Where-Object Outcome -eq 'Passed').Count
        $summary += "  $($group.Name): $ok passed / $($group.Count) results"
    }
    $lines = @()
    foreach ($failure in $failed) {
        $lines += "FAIL [$($failure.Suite)] $($failure.Name)"
        $lines += "     $($failure.Message)"
        if ($failure.Where) { $lines += "     at $($failure.Where)" }
    }
    $summary + "  Run: $($started.ToString('yyyy-MM-dd HH:mm:ss')); each suite's output: logs\<Suite>_Results\" + $lines |
        Set-Content -Encoding UTF8 (Join-Path $gateDir 'LAST_RESULT.txt')
    $summary | Write-Output
    $lines | Select-Object -First 36 | Write-Output
    if ($failed.Count -gt 12) { "More failures: logs\Gate_Results\LAST_RESULT.txt" | Write-Output }
    $result = if ($verdict -eq 'PASS') { 0 } else { 1 }
} catch {
    "AV GATE: FAIL - $($_.Exception.Message)" | Write-Output
    $result = 1
} finally {
    $env:FVS_TEST_NATIVE_MPV = $oldNative
    $env:FVS_TEST_OUTPUT_MANAGED = $oldManaged
    if ($lease) { $lease.Dispose() }
}
exit $result
