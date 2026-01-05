function Test-PythonSyntax {
    param([Parameter(Mandatory = $true)][string]$FilePath, [Parameter(Mandatory = $true)][string]$Content)
    if ($Content -match "`t") {
        Write-Host "[STYLE ERROR] $FilePath" -ForegroundColor Red
        Write-Host "    Tab character(s) detected. Convert tabs to spaces." -ForegroundColor Yellow
        return $false
    }
    $checkScript = @"
import sys
import re
def check_indentation():
    success = True
    try:
        lines = sys.stdin.readlines()
        bracket_level = 0
        last_logical_indent = 0
        for i, line in enumerate(lines):
            lnum = i + 1
            line = re.sub(r"\s+\\", "", line)
            stripped = line.lstrip()
            if not stripped or stripped.startswith('#'):
                continue
            current_indent = len(line) - len(stripped)
            if bracket_level == 0:
                if current_indent % 4 != 0:
                    print(f"ALIGNMENT ERROR: Line {lnum} has {current_indent} spaces (not a multiple of 4).")
                    success = False
                if current_indent > last_logical_indent + 4:
                    print(f"DEEP INDENT ERROR: Line {lnum} jumped from {last_logical_indent} to {current_indent} spaces.")
                    success = False
                last_logical_indent = current_indent
            clean_line = re.sub(r"'.*?'|\".*?\"", "", line)
            bracket_level += clean_line.count('(') + clean_line.count('[') + clean_line.count('{')
            bracket_level -= clean_line.count(')') + clean_line.count(']') + clean_line.count('}')
        try:
            compile("".join(lines), '<stdin>', 'exec')
        except (SyntaxError, IndentationError) as e:
            print(f"PYTHON PARSER ERROR: {e.msg} | Line: {e.lineno}")
            success = False
        return success
    except Exception as e:
        print(f"OTHER ERROR: {str(e)}")
        return False
if __name__ == '__main__':
    sys.exit(0 if check_indentation() else 1)
"@
    $result = $Content | python -X utf8 -c $checkScript 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[INVALID] $FilePath" -ForegroundColor Red
        if ($result) { $result | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow } }
        return $false
    }
    return $true
}

function Get-PythonCommentIndex {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Line, [Parameter(Mandatory=$true)][ref]$InTripleSingle, [Parameter(Mandatory=$true)][ref]$InTripleDouble)
    if ([string]::IsNullOrEmpty($Line)) { return -1 }
    $inSingle = $false; $inDouble = $false; $escape = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $ch = $Line[$i]
        if ($escape) { $escape = $false; continue }
        if ($InTripleSingle.Value) {
            if ($i -le $Line.Length - 3 -and $Line.Substring($i,3) -eq "'''") { $InTripleSingle.Value = $false; $i += 2 }
            continue
        }
        if ($InTripleDouble.Value) {
            if ($i -le $Line.Length - 3 -and $Line.Substring($i,3) -eq '"""') { $InTripleDouble.Value = $false; $i += 2 }
            continue
        }
        if (($inSingle -or $inDouble) -and $ch -eq '\') { $escape = $true; continue }
        if (-not ($inSingle -or $inDouble)) {
            if ($i -le $Line.Length - 3) {
                $tri = $Line.Substring($i,3)
                if ($tri -eq "'''") { $InTripleSingle.Value = $true; $i += 2; continue }
                if ($tri -eq '"""') { $InTripleDouble.Value = $true; $i += 2; continue }
            }
        }
        if (-not $inDouble -and $ch -eq "'") { $inSingle = -not $inSingle; continue }
        if (-not $inSingle -and $ch -eq '"') { $inDouble = -not $inDouble; continue }
        if (-not ($inSingle -or $inDouble) -and $ch -eq '#') { return $i }
    }
    return -1
}

function Get-FlaggedItemsForFile {
    param([Parameter(Mandatory=$true)][string[]]$Lines, [Parameter(Mandatory=$true)][string]$Extension, [string]$FilePath)
    $ext = $Extension.ToLowerInvariant()
    $items = New-Object System.Collections.Generic.List[object]
    $inTripleSingle = $false; $inTripleDouble = $false; $inPsBlock = $false
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $cur = $lines[$i]; $next = if ($i -lt $lines.Count - 1) { $lines[$i+1] } else { '' }
        $isEmpty = ($cur -match '^\s*$')
        $isShebang = ($i -eq 0 -and $cur -match '^#!')
        if ($isShebang) { continue }
        if ($ext -eq '.ps1') {
            $hasOpen = $cur.Contains('<#')
            $hasClose = $cur.Contains('#>')
            $isFullLineComment = $inPsBlock -or ($cur -match '^\s*\#') -or ($cur -match '^\s*<#')
            if ($hasOpen -and -not $hasClose) { $inPsBlock = $true }
            elseif ($hasClose -and -not $hasOpen) { $inPsBlock = $false }
            $hasBlockInlineComment = (-not $isFullLineComment -and ($cur -match '<#.*#>' -or $cur -match '<#'))
        } elseif ($ext -eq '.py') {
            $isFullLineComment = ($cur -match '^\s*\#')
            $hasBlockInlineComment = $false
        } else {
            $isFullLineComment = (($cur -match '^\s*//') -or ($cur -match '^\s*/\*') -or ($cur -match '^\s*\*'))
            $hasBlockInlineComment = ($cur -match '/\*.*\*/')
        }
        $hashIndex = -1; $hasHashInlineComment = $false
        if (($ext -eq '.py' -or $ext -eq '.ps1') -and -not $isEmpty -and -not $isFullLineComment) {
            $dummy = $false
            $refTripleSingle = if ($ext -eq '.py') { [ref]$inTripleSingle } else { [ref]$dummy }
            $refTripleDouble = if ($ext -eq '.py') { [ref]$inTripleDouble } else { [ref]$dummy }
            $hashIndex = Get-PythonCommentIndex -Line $cur -InTripleSingle $refTripleSingle -InTripleDouble $refTripleDouble
            $hasHashInlineComment = ($hashIndex -ge 0)
        }
        $isComment = ($isFullLineComment -or $hasBlockInlineComment -or $hasHashInlineComment)
        $isNextCode = ($next -match '^\s*(def|class|from|import)\b')
        if ($isComment -or ($isEmpty -and -not $isNextCode)) {
            $kindString = if ($isEmpty) { 'EMPTY' } else { 'COMMENT' }
            $items.Add([pscustomobject]@{
                FilePath             = $FilePath
                LineIndexZeroBased   = $i
                LineNumberOneBased   = ($i + 1)
                Kind                 = $kindString
                IsEmpty              = $isEmpty
                IsFullLineComment    = $isFullLineComment
                HasBlockInlineComment = $hasBlockInlineComment
                HasHashInlineComment = $hasHashInlineComment
                HashCommentIndex     = $hashIndex
                OriginalLine         = $cur
            })
        }
    }
    return $items
}

function Get-SurgicallyCleanedContent {
    param([Parameter(Mandatory=$true)][string[]]$Lines, [Parameter(Mandatory=$true)][string]$Extension, [System.Collections.Generic.List[object]]$FlaggedItems)
    $ext = $Extension.ToLowerInvariant()
    $lookup = if ($FlaggedItems -and $FlaggedItems.Count -gt 0) { $FlaggedItems | Group-Object LineIndexZeroBased -AsHashTable } else { @{} }
    $newLines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $its = if ($lookup.ContainsKey($i)) { $lookup[$i] } else { $null }
        if ($its -and ($its | Where-Object { $_.IsEmpty -or $_.IsFullLineComment })) { continue }
        $line = $lines[$i].Replace("`t", "    ")
        $line = [regex]::Replace($line, '\s+\\', '')
        $line = $line.TrimEnd()
        if ($its) {
            $hasBlock = $false; $hasHash = $false; $hashIndex = -1
            foreach ($it in $its) {
                if ($it.HasBlockInlineComment) { $hasBlock = $true }
                if ($it.HasHashInlineComment) { $hasHash = $true; $hashIndex = $it.HashCommentIndex }
            }
            if ($hasBlock) {
                if ($line -match '/\*.*\*/') { $line = [regex]::Replace($line, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) }
                if ($ext -eq '.ps1') {
                    if ($line -match '<#.*#>') { $line = [regex]::Replace($line, '<#.*?#>', '', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) }
                    elseif ($line -match '<#') { $line = $line.Split('<#')[0] }
                }
            }
            if ($hasHash -and ($ext -eq '.py' -or $ext -eq '.ps1') -and $hashIndex -ge 0) { $line = $line.Substring(0, $hashIndex) }
            $line = $line.TrimEnd()
        }
        $newLines.Add($line)
    }
    return $newLines -join [Environment]::NewLine
}

function Apply-SurgicalRemovalsForFile {
    param([string]$FilePath, [string]$CleanContent)
    Set-Content -LiteralPath $FilePath -Value $CleanContent -Encoding UTF8
}

Set-Location $PSScriptRoot
$excludedDirNames = @('binaries','config','logs','mp3','!!!_Ouput_Video_Files_!!!','venv','.git')
$excludedFileNames = @('app.py','Installer.ps1','project_structure.txt', $MyInvocation.MyCommand.Name)
$allowedExtensions = @('.py','.txt','.md','.json','.ini','.cfg','.yml','.yaml','.js','.ts','.html','.css','.qss','.bat','.cmd','.ps1')
Write-Host "--- PRE-FLIGHT: Generating Change Report ---`n" -ForegroundColor Cyan
$targetFiles = Get-ChildItem -Recurse -File | Where-Object {
    if ($excludedFileNames -contains $_.Name) { return $false }
    if ($allowedExtensions -notcontains $_.Extension.ToLowerInvariant()) { return $false }
    $dirParts = $_.DirectoryName -split '[\\/]'; foreach ($d in $excludedDirNames) { if ($dirParts -contains $d) { return $false } }; return $true
}
$report = New-Object System.Collections.Generic.List[string]
$pendingChanges = @{}
foreach ($file in $targetFiles) {
    $lines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)
    if ($null -eq $lines -or $lines.Count -eq 0) { continue }
    $original = $lines -join [Environment]::NewLine
    $flagged = Get-FlaggedItemsForFile -Lines $lines -Extension $file.Extension -FilePath $file.FullName
    $cleaned = Get-SurgicallyCleanedContent -Lines $lines -Extension $file.Extension -FlaggedItems $flagged
    if ($file.Extension -eq '.py' -and -not (Test-PythonSyntax -FilePath $file.FullName -Content $cleaned)) { continue }
    if ($original.Replace("`r`n", "`n") -ne $cleaned.Replace("`r`n", "`n")) {
        $pendingChanges[$file.FullName] = $cleaned
        $report.Add("--- $($file.FullName)")
		$report.Add("+++ $($file.FullName) (PROPOSED)")
        $origLines = $original -split '\r?\n'
        $newLines = $cleaned -split '\r?\n'
        $removeIdxs = New-Object 'System.Collections.Generic.HashSet[int]'
        foreach($f in $flagged) { if($f.IsEmpty -or $f.IsFullLineComment) { [void]$removeIdxs.Add($f.LineIndexZeroBased) } }
        $newPtr = 0
        for($j=0; $j -lt $origLines.Count; $j++) {
                $oldLine = $origLines[$j]
                if($removeIdxs.Contains($j)) {
                    $report.Add("- $oldLine")
                    continue
                }
                $newLine = if ($newPtr -lt $newLines.Count) { $newLines[$newPtr] } else { $null }
                if ($null -ne $newLine -and $oldLine -ne $newLine) {
                    $report.Add("- $oldLine")
                    $report.Add("+ $newLine")
                }
                $newPtr++
            }
            $report.Add("`n")
    }
}
if ($pendingChanges.Count -gt 0) {
    Write-Host "`n--- PROPOSED CHANGES ---" -ForegroundColor Cyan
    foreach ($line in $report) {
        if ($line.StartsWith('---') -or $line.StartsWith('+++')) {
            Write-Host $line -ForegroundColor White -BackgroundColor DarkGray
        } elseif ($line.StartsWith('-')) {
            Write-Host $line -ForegroundColor Red
        } elseif ($line.StartsWith('+')) {
            Write-Host $line -ForegroundColor Green
        } else {
            Write-Host $line
        }
    }
    Write-Host ("=" * 60)
    $answer = (Read-Host "Review the changes above. Apply all changes now? (Y/N)").Trim()
    if ($answer -match '^(Y|YES)$') {
        foreach ($path in $pendingChanges.Keys) {
            Apply-SurgicalRemovalsForFile -FilePath $path -CleanContent $pendingChanges[$path]
            Write-Host "Fixed: $path" -ForegroundColor Green
        }
    } else { Write-Host "Aborted. No files were touched." -ForegroundColor Red }
} else { Write-Host "Everything is already clean." -ForegroundColor Green }
pause