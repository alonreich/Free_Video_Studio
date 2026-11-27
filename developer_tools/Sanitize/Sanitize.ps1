param([ValidateSet('fix', 'check', 'history')][string]$Mode = 'fix')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$exe = Join-Path $here 'ai-sanitize.exe'
$allowFile = Join-Path $here 'allowlist.txt'
$source = $env:FVS_SANITIZE_SOURCE
if (-not $source) { $source = 'C:\Software_Development\Sanitization\Compiled\ai-sanitize.exe' }
if (Test-Path -LiteralPath $source -PathType Container) { $source = Join-Path $source 'ai-sanitize.exe' }
function Write-Mark([string]$Kind, [string]$Text) {
    $color = switch ($Kind) { 'LEAK' { 'Red' } 'LOCAL' { 'Yellow' } 'CHECK' { 'Yellow' } 'FIXED' { 'Green' } 'OK' { 'Green' } default { 'Gray' } }
    $previous = [Console]::ForegroundColor
    [Console]::ForegroundColor = $color
    [Console]::Out.WriteLine(('{0,-6}{1}' -f $Kind, $Text))
    [Console]::ForegroundColor = $previous
}
if ((Test-Path -LiteralPath $source -PathType Leaf) -and ((-not (Test-Path -LiteralPath $exe)) -or ((Get-Item -LiteralPath $source).LastWriteTimeUtc -gt (Get-Item -LiteralPath $exe).LastWriteTimeUtc))) {
    Copy-Item -LiteralPath $source -Destination $exe -Force
    Write-Mark 'INFO' "Refreshed ai-sanitize.exe from $source"
}
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Mark 'LEAK' "ai-sanitize.exe is missing in $here and FVS_SANITIZE_SOURCE does not name one. Nothing could be checked."
    exit 2
}
$allow = @()
if (Test-Path -LiteralPath $allowFile) {
    foreach ($line in Get-Content -LiteralPath $allowFile -Encoding UTF8) {
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        $rule = @{}
        foreach ($part in $line.Split(';')) {
            $pair = $part.Split('=', 2)
            if ($pair.Count -eq 2) { $rule[$pair[0].Trim().ToLowerInvariant()] = $pair[1].Trim() }
        }
        if ($rule.Count -gt 0) { $allow += , $rule }
    }
}
function Test-Allowed($Finding) {
    foreach ($rule in $allow) {
        $hit = $true
        foreach ($key in $rule.Keys) {
            $actual = [string]$Finding.$key
            if (-not ($actual -like $rule[$key])) { $hit = $false; break }
        }
        if ($hit) { return $true }
    }
    return $false
}
$sensitiveNames = @('*.pfx', '*.p12', '*.snk', '*.key.pem', '*.password.txt', '*.kdbx', '*.jks', '*.keystore', '*.dmp', '*.mdmp', '.env', '*.env', 'id_rsa*', 'id_ed25519*', 'credentials*.json', '*.ppk')
function Test-SensitiveName([string]$Name) {
    foreach ($pattern in $sensitiveNames) { if ($Name -like $pattern) { return $true } }
    return $false
}
function Invoke-Scan {
    $raw = & $exe scan $repo --low --json 2>$null
    $code = $LASTEXITCODE
    if ($code -eq 2 -or -not $raw) { Write-Mark 'LEAK' 'ai-sanitize.exe scan failed.'; exit 2 }
    $doc = ($raw -join "`n") | ConvertFrom-Json
    $items = New-Object System.Collections.Generic.List[object]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $doc.projects) {
        foreach ($f in $project.findings) {
            $items.Add([pscustomobject]@{ id = $f.id; severity = [string]$f.severity; category = [string]$f.category; value = [string]$f.value; path = [string]$f.path; state = [string]$f.state; where = (@($f.where) -join ','); canFix = [bool]$f.canFix; needsHistory = [bool]$f.needsHistory; onlyOnRemote = [bool]$f.onlyOnRemote; fix = [string]$f.fix })
            if ($f.path) { [void]$seen.Add(([string]$f.path).Replace('\', '/')) }
        }
    }
    Push-Location $repo
    try {
        $files = Get-ChildItem -LiteralPath $repo -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike (Join-Path $repo '.git\*') -and (Test-SensitiveName $_.Name) }
        foreach ($file in $files) {
            $rel = $file.FullName.Substring($repo.Length + 1).Replace('\', '/')
            if ($seen.Contains($rel)) { continue }
            [void]$seen.Add($rel)
            & git check-ignore -q -- $rel 2>$null
            $state = 'gitignored-never-committed'
            if ($LASTEXITCODE -ne 0) { $state = 'tracked' }
            $items.Add([pscustomobject]@{ id = ''; severity = 'critical'; category = 'Sensitive file'; value = $rel; path = $rel; state = $state; where = 'file'; canFix = $false; needsHistory = $false; onlyOnRemote = $false; fix = 'Move it outside the repository.' })
        }
        $everAdded = & git log --all --diff-filter=A --name-only --format= 2>$null | Sort-Object -Unique
        foreach ($rel in $everAdded) {
            if (-not $rel) { continue }
            if (-not (Test-SensitiveName ([System.IO.Path]::GetFileName($rel)))) { continue }
            if ($seen.Contains($rel)) { continue }
            [void]$seen.Add($rel)
            $items.Add([pscustomobject]@{ id = ''; severity = 'critical'; category = 'Sensitive file in git history'; value = $rel; path = $rel; state = 'history-only'; where = 'history'; canFix = $false; needsHistory = $true; onlyOnRemote = $false; fix = 'Purge it from history (git filter-repo --invert-paths --path <file>) and rotate it.' })
        }
    }
    finally { Pop-Location }
    return , $items
}
function Get-Kind($Finding) {
    if (Test-Allowed $Finding) { return 'ALLOW' }
    if ($Finding.severity -eq 'low') { return 'CHECK' }
    if ($Finding.state -eq 'gitignored-never-committed') { return 'LOCAL' }
    return 'LEAK'
}
function Show-Report($Items) {
    $script:remoteOnly = 0
    $counts = @{ LEAK = 0; LOCAL = 0; CHECK = 0; ALLOW = 0 }
    foreach ($f in ($Items | Sort-Object @{ Expression = { switch (Get-Kind $_) { 'LEAK' { 0 } 'LOCAL' { 1 } 'CHECK' { 2 } default { 3 } } } }, path)) {
        $kind = Get-Kind $f
        $counts[$kind]++
        if ($kind -eq 'LEAK' -and $f.onlyOnRemote) { $script:remoteOnly++ }
        if ($kind -eq 'ALLOW') { continue }
        $where = $f.path
        if (-not $where) { $where = $f.where }
        $note = ''
        if ($f.onlyOnRemote) { $note = ' (already clean here; GitHub still has it until you force-push)' }
        Write-Mark $kind ("{0} | {1} | {2} | {3}{4}" -f $f.category, $f.value, $where, $f.state, $note)
    }
    Write-Mark 'INFO' ("{0} leak(s) that can reach GitHub, {1} local-only, {2} to check by eye, {3} accepted by allowlist.txt" -f $counts.LEAK, $counts.LOCAL, $counts.CHECK, $counts.ALLOW)
    return $counts.LEAK
}
Write-Mark 'INFO' "Scanning $repo (working tree, hidden folders, every branch, tag and commit)..."
$items = Invoke-Scan
$leaks = Show-Report $items
if ($Mode -eq 'check') {
    if ($leaks -gt 0) {
        if ($script:remoteOnly -gt 0) { Write-Mark 'LEAK' ("{0} of them are already gone here but still on GitHub: git push --force origin main, then git push --force --tags." -f $script:remoteOnly) }
        if ($leaks -gt $script:remoteOnly) { Write-Mark 'LEAK' 'Run developer_tools\Sanitize.cmd to fix the rest (add history to also rewrite git history).' }
        exit 1
    }
    Write-Mark 'OK' 'Nothing sensitive can reach GitHub.'
    exit 0
}
$fileIds = @($items | Where-Object { $_.id -and $_.canFix -and -not $_.needsHistory -and (Get-Kind $_) -ne 'ALLOW' -and (Get-Kind $_) -ne 'CHECK' } | ForEach-Object { $_.id })
$historyIds = @($items | Where-Object { $_.id -and $_.canFix -and $_.needsHistory -and (Get-Kind $_) -eq 'LEAK' } | ForEach-Object { $_.id })
if ($fileIds.Count -gt 0) {
    $fixArgs = @('fix', $repo)
    foreach ($id in $fileIds) { $fixArgs += @('--id', $id) }
    & $exe @fixArgs | Out-Null
    Write-Mark 'FIXED' ("Cleaned {0} item(s) in files." -f $fileIds.Count)
}
if ($historyIds.Count -gt 0) {
    if ($Mode -ne 'history') {
        Write-Mark 'LEAK' ("{0} item(s) live in git history. Run developer_tools\Sanitize.cmd history to rewrite it (a backup bundle is made first)." -f $historyIds.Count)
    }
    else {
        $dirty = & git -C $repo status --porcelain --untracked-files=no
        if ($dirty) {
            Write-Mark 'LEAK' 'History was NOT rewritten: commit or stash your changes first.'
        }
        else {
            $bundle = Join-Path $env:USERPROFILE ('FreeVideoStudio-backup-{0}.bundle' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
            & git -C $repo bundle create $bundle --all 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bundle)) {
                Write-Mark 'LEAK' 'History was NOT rewritten: the backup bundle could not be created.'
            }
            else {
                Write-Mark 'INFO' "Backup: $bundle"
                $fixArgs = @('fix', $repo, '--history')
                foreach ($id in $historyIds) { $fixArgs += @('--id', $id) }
                & $exe @fixArgs | Out-Null
                Write-Mark 'FIXED' ("Rewrote history for {0} item(s). Nothing was pushed. To publish: git push --force origin main, then git push --force --tags. Other clones must re-clone." -f $historyIds.Count)
            }
        }
    }
}
if ($fileIds.Count -gt 0 -or ($Mode -eq 'history' -and $historyIds.Count -gt 0)) {
    Write-Mark 'INFO' 'Re-scanning...'
    $items = Invoke-Scan
    $leaks = Show-Report $items
}
if ($leaks -gt 0) {
    if ($script:remoteOnly -gt 0) { Write-Mark 'LEAK' ("{0} of them are already gone here but still on GitHub: git push --force origin main, then git push --force --tags." -f $script:remoteOnly) }
    exit 1
}
Write-Mark 'OK' 'Nothing sensitive can reach GitHub.'
exit 0
