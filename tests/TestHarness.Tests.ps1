# Executable regression tests for TESTGATE_01 / TESTCLEAN_01; no Pester dependency.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestHarness.ps1')
$repo = Split-Path -Parent $PSScriptRoot
# TESTOUT_01: this suite's own output folder is logs\Harness_Results\work (a fake repository).
$fixture = Enter-SuiteOutput $repo 'Harness'
$null = New-Item -ItemType Directory -Force (Join-Path $fixture 'tests')
[IO.File]::WriteAllText((Join-Path $fixture 'FreeVideoStudio.sln'), 'fixture repository')
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }; $script:checks++
}
function Refuses([scriptblock]$Action, [string]$Message) {
    $rejected = $false; try { & $Action } catch { $rejected = $true }
    Check $rejected $Message
}
function Put([string]$Path, [string]$Text = 'fixture') {
    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $Path)
    [IO.File]::WriteAllText($Path, $Text)
}
function Trx([string]$Outcome = 'Passed', [string]$Summary = 'Completed', [int]$Total = 1) {
    $passed = if ($Outcome -eq 'Passed') { 1 } else { 0 }
    $executed = if ($Outcome -eq 'NotExecuted') { 0 } else { 1 }
    "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results><UnitTestResult testName='Suite.S01_Example' outcome='$Outcome'/></Results><ResultSummary outcome='$Summary'><Counters total='$Total' executed='$executed' passed='$passed'/></ResultSummary></TestRun>"
}
$trx = Join-Path $fixture 'result.trx'
Put $trx (Trx)
Check (@(Read-TestResults $trx AV 0 @('S01') | Where-Object Outcome -ne 'Passed').Count -eq 0) 'A complete successful TRX was rejected.'
Check (@(Read-TestResults $trx AV 1 | Where-Object Outcome -eq 'Failed').Count -gt 0) 'Nonzero process exit was ignored.'
Check (@(Read-TestResults $trx AV 0 @('S02') | Where-Object Outcome -eq 'Failed').Count -gt 0) 'A missing required scenario passed.'
foreach ($bad in @('<TestRun><Results /></TestRun>', '<broken', (Trx NotExecuted),
    (Trx Failed), (Trx Passed Aborted), (Trx Passed Completed 2),
    ((Trx) -replace '</ResultSummary>', "<RunInfos><RunInfo outcome='Error'/></RunInfos></ResultSummary>"),
    '<!DOCTYPE TestRun [<!ENTITY x SYSTEM "file:///no-read">]><TestRun>&x;</TestRun>')) {
    Put $trx $bad
    Check (@(Read-TestResults $trx AV 0 | Where-Object Outcome -eq 'Failed').Count -gt 0) 'An incomplete/invalid TRX passed.'
}
Check (@(Read-TestResults (Join-Path $fixture 'missing.trx') AV 0 | Where-Object Outcome -eq 'Failed').Count -gt 0) 'Missing TRX passed.'

$owned = Join-Path $fixture 'logs\AV_Results'
$outside = Join-Path $fixture 'user-media'
Put (Join-Path $outside 'keep.mp4')
Put (Join-Path $owned 'work\old.bin')
Put (Join-Path $owned 'media\old.mp4')
Put (Join-Path $owned 'state\settings.json')
Put (Join-Path $owned 'LAST_RESULT.txt')
Refuses { Remove-TestOutput $fixture $fixture } 'Deleting the repository root was accepted.'
Refuses { Remove-TestOutput $fixture $outside } 'Deleting user media was accepted.'
Refuses { Remove-TestOutput $fixture ($owned + '-other') } 'A sibling-prefix path was accepted.'
Refuses { Remove-TestOutput $fixture (Join-Path $owned '..\user-media') } 'Traversal was accepted.'
Refuses { Remove-TestOutput $fixture (Join-Path $fixture 'logs') } 'Deleting the whole logs folder was accepted.'
Refuses { Remove-TestOutput $fixture (Join-Path $fixture 'TestResults') } 'A retired output folder was accepted.'
Refuses { Remove-TestOutput $fixture (Join-Path $fixture 'logs\Bad_Name_Results') } 'A malformed suite folder was accepted.'
Refuses { Reset-TestOutputs $fixture @('..\x') } 'A traversing suite name was accepted.'
$null = New-Item -ItemType Junction -Path (Join-Path $owned 'outside-link') -Target $outside
Remove-TestOutput $fixture $owned
Check (-not (Test-Path -LiteralPath $owned)) 'Previous output/media/state survived cleanup.'
Check (Test-Path -LiteralPath (Join-Path $outside 'keep.mp4')) 'Cleanup followed a child junction.'
$null = New-Item -ItemType Junction -Path $owned -Target $outside
Refuses { Reset-TestOutputs $fixture @('AV') } 'A linked output root was accepted.'
Check (Test-Path -LiteralPath (Join-Path $outside 'keep.mp4')) 'Root-link rejection modified the target.'
# Unlink this verified fixture junction itself; never recurse into its target.
[IO.Directory]::Delete($owned)
$lock = Enter-TestRun $fixture
try { Refuses { $other = Enter-TestRun $fixture; $other.Dispose() } 'Concurrent runs can erase each other.' }
finally { $lock.Dispose() }
$lock = Enter-TestRun $fixture; $lock.Dispose()
Put (Join-Path $owned 'work\locked.bin')
$handle = [IO.File]::Open((Join-Path $owned 'work\locked.bin'), 'Open', 'ReadWrite', 'None')
try { Refuses { Remove-TestOutput $fixture $owned } 'Locked output deletion was silently ignored.' }
finally { $handle.Dispose() }
Remove-TestOutput $fixture $owned
Put (Join-Path $owned 'current.txt')
Check (Test-Path -LiteralPath (Join-Path $owned 'current.txt')) 'Current results were not writable after cleanup.'
Write-Output "Harness: PASS ($checks result-validation and cleanup checks)."
