# Behavioural tests: real filesystem cleanup, preservation, locked files and junction boundaries.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'build\DevSandbox.ps1')
. (Join-Path $repo 'tests\TestHarness.ps1')
# TESTOUT_01: this suite's own output folder is logs\DevSandbox_Results\work.
$testRoot = Enter-SuiteOutput $repo 'DevSandbox'
$data = Join-Path $testRoot 'dev\data'
$keep = Join-Path $testRoot 'dev\keep'
$old = Join-Path $testRoot 'old\data'
$oldMedia = Join-Path $testRoot 'old\media'
$production = Join-Path $testRoot 'production'

function Put([string]$Path, [string]$Content = 'fixture') {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Must-Refuse([scriptblock]$Action, [string]$Message) {
    $refused = $false
    try { & $Action } catch { $refused = $true }
    Check $refused $Message
}

Put (Join-Path $production 'settings.json') '{"theme":"production"}'
Put (Join-Path $keep 'settings.json') '{"theme":"dev","SchemaVersion":1}'
Put (Join-Path $keep 'session_state.json') '{"window_bounds":{"width":1234}}'
Put (Join-Path $oldMedia 'Memes\sample.png')
Initialize-DevSandbox $data $old $keep $oldMedia
Check ((Get-Content (Join-Path $data 'settings.json') -Raw) -match '"theme":"dev"') 'Must restore only remembered DEVELOPMENT settings.'
Check (Test-Path (Join-Path $data 'media\Memes\sample.png')) 'Must retain old development media.'

$preserved = @('settings.json', 'session_state.json', 'crops_coordinations.conf',
    'uistate\meme_picker.json', 'uistate\tour.txt', 'media\Memes\sample.png',
    'voiceovers\take.wav', 'exports\finished.mp4', 'recovery_v2.json',
    'scratch\work\voiceover_take.wav', 'scratch\work\voiceover_recovery_sample.json',
    'scratch\work\FreeVideoStudio-RECOVERED-sample.mp4')
foreach ($name in $preserved) { if (-not (Test-Path (Join-Path $data $name))) { Put (Join-Path $data $name) } }
$hashes = @{}
foreach ($name in $preserved) { $hashes[$name] = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $data $name))) }
$caches = @('cache\lanes\old.png', 'updates\Downloads\old.exe', 'logs\old.log',
    'uistate\meme_dimensions.json', 'uistate\update_last_result.txt', 'uistate\update_pending_notes.txt',
    'scratch\os\old.jpg', 'scratch\work\voice-preview-old.mkv')
foreach ($name in $caches) { Put (Join-Path $data $name) }
Clear-DevRuntimeCaches $data
foreach ($name in $caches) { Check (-not (Test-Path (Join-Path $data $name))) "Cache survived: $name" }
foreach ($name in $preserved) { Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $data $name))) -eq $hashes[$name]) "User data changed: $name" }

# A second boot cannot overwrite changed settings from an old backup.
Put (Join-Path $data 'settings.json') '{"theme":"changed"}'
Initialize-DevSandbox $data $old $keep $oldMedia
Check ((Get-Content (Join-Path $data 'settings.json') -Raw) -match 'changed') 'Current settings must win.'
Check ((Get-Content (Join-Path $production 'settings.json') -Raw) -match 'production') 'Production fixture changed.'

Must-Refuse { Remove-DevCache $production $data } 'Outside-root deletion was accepted.'
Must-Refuse { Remove-DevCache ($data + '-other') $data } 'Sibling prefix was accepted.'
Must-Refuse { Remove-DevCache $data $data } 'Deleting the settings root was accepted.'

$locked = Join-Path $data 'cache\locked.bin'
Put $locked
$handle = [IO.File]::Open($locked, 'Open', 'ReadWrite', 'None')
try { Must-Refuse { Clear-DevRuntimeCaches $data } 'Locked-cache cleanup must fail the launch.' }
finally { $handle.Dispose() }
Check (Test-Path $locked) 'Locked file unexpectedly removed.'

$link = Join-Path $data 'cache\outside'
New-Item -ItemType Junction -Path $link -Target $production | Out-Null
Must-Refuse { Clear-DevRuntimeCaches $data } 'A junction into production was followed.'
Check (Test-Path (Join-Path $production 'settings.json')) 'Junction target was deleted.'
$output = Join-Path $testRoot 'runtime'
$app = Join-Path $output 'app'
foreach ($name in @('app\backend\codec.dll', 'app\frontend\player.dll', 'app\starter\meme\sample.png', 'build\obj\compiler.bin', 'app\obsolete.dll', 'app\old-package\obsolete.dll')) {
    Put (Join-Path $output $name)
}
Clear-DevManagedOutput $app $output
foreach ($name in @('backend\codec.dll', 'frontend\player.dll', 'starter\meme\sample.png')) {
    Check (Test-Path (Join-Path $app $name)) "Retained asset lost: $name"
}
Check (Test-Path (Join-Path $output 'build\obj\compiler.bin')) 'Compiler intermediates were lost.'
Check (-not (Test-Path (Join-Path $app 'obsolete.dll'))) 'Obsolete managed assembly survived.'
Check (-not (Test-Path (Join-Path $app 'old-package'))) 'Obsolete package directory survived.'
# DEVHELPERTEMP_01: an old console/native helper retains a handle after UI closure.
$helperHome = Join-Path $testRoot 'helpers'
$helperRoot = Join-Path $helperHome 'scratch\os'
$helperLocked = Join-Path $helperRoot 'old-console.tmp'
$helperStale = Join-Path $helperRoot 'old-run\stale.tmp'
Put $helperLocked
Put $helperStale
$handle = [IO.File]::Open($helperLocked, 'Open', 'ReadWrite', 'None')
try {
    $first = New-DevHelperScratch $helperRoot $helperHome
    $second = New-DevHelperScratch $helperRoot $helperHome
    Check ($first -ne $second) 'Each launch must get a fresh helper directory.'
    Check (Test-Path $helperLocked) 'Locked helper file must remain untouched.'
    Check (-not (Test-Path $helperStale)) 'Unlocked helper leftovers must be removed.'
    Check (@(Get-ChildItem -LiteralPath $second -Force).Count -eq 0) 'New launch reused stale helper data.'
} finally { $handle.Dispose() }
$third = New-DevHelperScratch $helperRoot $helperHome
Check (-not (Test-Path $helperLocked)) 'Released helper lock must be cleaned on the next launch.'
Must-Refuse { New-DevHelperScratch $production $helperHome } 'Helper cleanup escaped its boundary.'
New-Item -ItemType Junction -Path (Join-Path $helperRoot 'outside') -Target $production | Out-Null
Must-Refuse { New-DevHelperScratch $helperRoot $helperHome } 'Helper cleanup followed a junction.'
Check (Test-Path (Join-Path $production 'settings.json')) 'Helper cleanup touched production.'
Write-Host 'Dev sandbox: preservation, managed refresh, retained dependencies, path/lock/junction guards and locked helper relaunch passed.'
# Small synthetic fixtures stay in logs\DevSandbox_Results\work until this suite runs again.
