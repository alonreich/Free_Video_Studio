# [SPEC CONTRACT] STRICT GOVERNANCE:
# Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-DEVBUILD, DEVDATA_01)
# Invariants, constants, and threading models must match spec bit-for-bit.
#
# DEVDATA_01 - keeps the dev.cmd sandbox's settings and AI keys between runs.
#
# Called by dev.cmd AFTER KILL_STALE (no app process may hold settings.json) and BEFORE launch.
#   1. One-time move: the sandbox used to live in %TMP%\FreeVideoStudio_DEV\.dev_data. %TMP% is
#      emptied by Windows Storage Sense / Disk Cleanup, which silently threw away the dev settings
#      and the Gemini key. If the new folder has no settings yet, the old one is copied across.
#   2. AI settings are remembered in keep\ai_settings.json beside the sandbox (outside the folder
#      'dev fresh' wipes) and put back when settings.json is missing or has no key. If nothing is
#      remembered yet, the key is borrowed once from the installed app's own settings.
# The script never removes anything and never fails the dev run: every problem is a warning.
param(
    [Parameter(Mandatory = $true)][string]$DevData,
    [Parameter(Mandatory = $true)][string]$OldDevData,
    [Parameter(Mandatory = $true)][string]$KeepDir,
    [Parameter(Mandatory = $true)][string]$ProdData
)

$ErrorActionPreference = 'Stop'
$aiFields = @('GeminiApiKey', 'GeminiModelName', 'AiMagicWandCloudConsent',
              'AiZoomBaseScale', 'AiZoomMinScale', 'AiZoomAvoidHud', 'AiZoomDeadbandPercent')
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Json([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8) | ConvertFrom-Json }
    catch { Write-Host "[DEV] WARNING: could not read $path ($($_.Exception.Message))"; return $null }
}

function Write-Json([string]$path, $object) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $tmp = "$path.devsandbox.tmp"
    [System.IO.File]::WriteAllText($tmp, ($object | ConvertTo-Json -Depth 32), $utf8)
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Has-Key($settings) {
    return $settings -and $settings.PSObject.Properties['GeminiApiKey'] -and
           -not [string]::IsNullOrWhiteSpace([string]$settings.GeminiApiKey)
}

try {
    $devSettings = Join-Path $DevData 'settings.json'
    $keepFile = Join-Path $KeepDir 'ai_settings.json'

    # 1. One-time move out of %TMP%. The marker keeps a later 'dev fresh' from importing it again.
    $moved = Join-Path $KeepDir 'moved_from_tmp.txt'
    if (-not (Test-Path -LiteralPath $moved)) {
        if (-not (Test-Path -LiteralPath $devSettings) -and (Test-Path -LiteralPath (Join-Path $OldDevData 'settings.json'))) {
            Write-Host "[DEV] Moving the dev sandbox out of %TMP% (Windows cleans %TMP%): $OldDevData -> $DevData"
            New-Item -ItemType Directory -Path $DevData -Force | Out-Null
            Copy-Item -Path (Join-Path $OldDevData '*') -Destination $DevData -Recurse -Force
        }
        New-Item -ItemType Directory -Path $KeepDir -Force | Out-Null
        [System.IO.File]::WriteAllText($moved, (Get-Date -Format o), $utf8)
    }

    $dev = Read-Json $devSettings

    # 2a. Remember the current AI settings whenever the sandbox has a key.
    if (Has-Key $dev) {
        $keep = [ordered]@{ SchemaVersion = $dev.SchemaVersion }
        foreach ($f in $aiFields) { if ($dev.PSObject.Properties[$f]) { $keep[$f] = $dev.$f } }
        Write-Json $keepFile ([pscustomobject]$keep)
        return
    }

    # 2b. No key in the sandbox: put back what was remembered, or borrow the installed app's key.
    $source = Read-Json $keepFile
    $from = 'the remembered dev AI settings'
    if (-not (Has-Key $source)) {
        $source = Read-Json (Join-Path $ProdData 'settings.json')
        $from = 'the installed app''s settings'
    }
    if (-not (Has-Key $source)) {
        Write-Host '[DEV] No Gemini key saved anywhere yet - add it once in Settings; dev.cmd keeps it from then on.'
        return
    }

    if ($null -eq $dev) {
        # Missing settings file ('dev fresh' or first run): a minimal file. Every other setting
        # takes the app's own default; SchemaVersion is the source's, so normal migration applies.
        $dev = [pscustomobject]@{ SchemaVersion = $source.SchemaVersion }
    }
    foreach ($f in $aiFields) {
        if ($source.PSObject.Properties[$f]) {
            $dev | Add-Member -NotePropertyName $f -NotePropertyValue $source.$f -Force
        }
    }
    Write-Json $devSettings $dev
    $keep = [ordered]@{ SchemaVersion = $source.SchemaVersion }
    foreach ($f in $aiFields) { if ($source.PSObject.Properties[$f]) { $keep[$f] = $source.$f } }
    Write-Json $keepFile ([pscustomobject]$keep)
    Write-Host "[DEV] Restored the Gemini key and AI settings from $from."
}
catch {
    Write-Host "[DEV] WARNING: dev settings sandbox step skipped: $($_.Exception.Message)"
}
exit 0
