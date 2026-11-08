# [SPEC CONTRACT] STRICT GOVERNANCE:
# Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-DEVBUILD).
# Invariants, constants, and threading models must match spec bit-for-bit.
# DEVDATA_01 / DEVCLEAN_01: only disposable paths are removed. Never import production settings.

function Assert-DevOwnedPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside development boundary: $full"
    }
    # Inspect every existing ancestor as well as descendants: junctions must not escape the sandbox.
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked path: $cursor" }
        }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $full -PathType Container) {
        $stack = New-Object 'System.Collections.Generic.Stack[string]'
        $stack.Push($full)
        while ($stack.Count) {
            foreach ($entry in Get-ChildItem -LiteralPath $stack.Pop() -Force -ErrorAction Stop) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked path: $($entry.FullName)" }
                if ($entry.PSIsContainer) { $stack.Push($entry.FullName) }
            }
        }
    }
    return $full
}

function Remove-DevCache([string]$Path, [string]$Root) {
    $checked = Assert-DevOwnedPath $Path $Root
    if (Test-Path -LiteralPath $checked) {
        Remove-Item -LiteralPath $checked -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $checked) { throw "Cleanup incomplete: $checked" }
    }
}

function Clear-DevManagedOutput([string]$App, [string]$Output) {
    # DEVINCREMENTAL_01: republish into a clean managed output, retaining only stager-owned assets.
    $null = Assert-DevOwnedPath $App $Output
    if (Test-Path -LiteralPath $App) {
        Get-ChildItem -LiteralPath $App -Force | Where-Object {
            -not ($_.PSIsContainer -and $_.Name -in @('backend', 'frontend', 'starter'))
        } | ForEach-Object { Remove-DevCache $_.FullName $Output }
    }
}

function New-DevHelperScratch([string]$HelperRoot, [string]$Boundary) {
    # DEVHELPERTEMP_01: consoles/native helpers may retain OS temp handles after UI exit.
    # Never reuse their directory, kill their owner, or weaken ordinary app-cache cleanup.
    $checked = Assert-DevOwnedPath $HelperRoot $Boundary
    $deferred = 0
    if (Test-Path -LiteralPath $checked) {
        foreach ($file in Get-ChildItem -LiteralPath $checked -File -Recurse -Force) {
            try { [IO.File]::Delete($file.FullName) }
            catch {
                $cause = $_.Exception.GetBaseException()
                $code = $cause.HResult -band 0xffff
                if ($cause -is [IO.IOException] -and $code -in @(32, 33)) { $deferred++; continue }
                throw
            }
        }
        foreach ($dir in (Get-ChildItem -LiteralPath $checked -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending)) {
            if (@(Get-ChildItem -LiteralPath $dir.FullName -Force).Count -eq 0) {
                try { [IO.Directory]::Delete($dir.FullName, $false) }
                catch {
                    $cause = $_.Exception.GetBaseException()
                    if ($cause -is [IO.IOException] -and ($cause.HResult -band 0xffff) -in @(32, 33)) { $deferred++; continue }
                    throw
                }
            }
        }
    }
    if ($deferred) { Write-Host "[DEV] Deferred $deferred locked helper-temp item(s); this launch uses a fresh folder." }
    $fresh = Assert-DevOwnedPath (Join-Path $checked ([Guid]::NewGuid().ToString('N'))) $checked
    New-Item -ItemType Directory -Path $fresh -Force | Out-Null
    return $fresh
}

function Initialize-DevSandbox([string]$DevData, [string]$OldDevData, [string]$KeepDir, [string]$OldMedia) {
    $null = Assert-DevOwnedPath $DevData (Split-Path -Parent $DevData)
    New-Item -ItemType Directory -Path $DevData -Force | Out-Null
    # Recover only earlier DEVELOPMENT preferences. Copy-only, absent destinations only.
    foreach ($name in @('settings.json', 'session_state.json')) {
        $target = Join-Path $DevData $name
        if (-not (Test-Path -LiteralPath $target)) {
            foreach ($sourceRoot in @($KeepDir, $OldDevData)) {
                $source = Join-Path $sourceRoot $name
                if (Test-Path -LiteralPath $source) {
                    $null = Assert-DevOwnedPath $source $sourceRoot
                    Copy-Item -LiteralPath $source -Destination $target -ErrorAction Stop
                    break
                }
            }
        }
    }
    # Old dev media lived in TEMP. Preserve it outside the disposable scratch area.
    $media = Join-Path $DevData 'media'
    if (-not (Test-Path -LiteralPath $media) -and (Test-Path -LiteralPath $OldMedia)) {
        $null = Assert-DevOwnedPath $OldMedia (Split-Path -Parent $OldMedia)
        Copy-Item -LiteralPath $OldMedia -Destination $media -Recurse -ErrorAction Stop
    }
}

function Clear-DevRuntimeCaches([string]$DevData) {
    $null = Assert-DevOwnedPath $DevData (Split-Path -Parent $DevData)
    foreach ($name in @('cache', 'updates', 'logs', 'uistate\meme_dimensions.json',
        'uistate\update_last_check_utc.txt', 'uistate\update_last_result.txt', 'uistate\update_pending_notes.txt')) {
        Remove-DevCache (Join-Path $DevData $name) $DevData
    }
    $scratch = Join-Path $DevData 'scratch'
    $null = Assert-DevOwnedPath $scratch $DevData
    if (Test-Path -LiteralPath $scratch) {
        # Voice recordings/recovery indexes and rescued exports are user work, even in scratch.
        # Keep them AT THEIR ORIGINAL PATH so saved recovery references remain valid.
        Get-ChildItem -LiteralPath $scratch -File -Recurse -Force | Where-Object {
            $_.Name -notlike 'voiceover_*' -and $_.Name -notlike '*RECOVERED*' -and $_.Extension -ne '.wav'
        } | ForEach-Object { Remove-DevCache $_.FullName $scratch }
    }
}
