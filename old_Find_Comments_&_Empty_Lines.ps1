function Test-PythonSyntax {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    $content = Get-Content -LiteralPath $FilePath -Raw -ErrorAction Stop
    if ($content -match "`t") {
        Write-Host "[STYLE ERROR] $FilePath" -ForegroundColor Red
        Write-Host "    Tab character(s) detected. Convert tabs to spaces." -ForegroundColor Yellow
        return $false
    }
    $checkScript = @"
import sys
import re
def check_indentation(filepath):
    success = True
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
        bracket_level = 0
        last_logical_indent = 0
        for i, line in enumerate(lines):
            lnum = i + 1
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
            compile("".join(lines), filepath, 'exec')
        except (SyntaxError, IndentationError) as e:
            print(f"PYTHON PARSER ERROR: {e.msg} | Line: {e.lineno}")
            success = False
        return success
    except Exception as e:
        print(f"OTHER ERROR: {str(e)}")
        return False
if __name__ == '__main__':
    sys.exit(0 if check_indentation(sys.argv[1]) else 1)
"@
    $result = $checkScript | python - $FilePath 2>$null
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
    param([Parameter(Mandatory=$true)][string]$FilePath)
    $ext = [IO.Path]::GetExtension($FilePath).ToLowerInvariant()
    $lines = Get-Content -LiteralPath $FilePath
    $items = New-Object System.Collections.Generic.List[object]
    $inTripleSingle = $false; $inTripleDouble = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $cur = $lines[$i]; $next = if ($i -lt $lines.Count - 1) { $lines[$i+1] } else { '' }
        $isEmpty = ($cur -match '^\s*$')
        $isFullLineComment = (($cur -match '^\s*\#') -or ($cur -match '^\s*\*\*') -or ($cur -match '^\s*//') -or ($cur -match '^\s*;') -or ($cur -match '^\s*/\*') -or ($cur -match '^\s*\*/') -or ($cur -match '^\s*\*'))
        $hasBlockInlineComment = ($cur -match '/\*.*\*/')
        $hashIndex = -1; $hasHashInlineComment = $false
        if ($ext -eq '.py' -and -not $isEmpty) {
            $hashIndex = Get-PythonCommentIndex -Line $cur -InTripleSingle ([ref]$inTripleSingle) -InTripleDouble ([ref]$inTripleDouble)
            $hasHashInlineComment = ($hashIndex -ge 0)
        }
        $isComment = ($isFullLineComment -or $hasBlockInlineComment -or $hasHashInlineComment)
        $isNextCode = ($next -match '^\s*(def|class|from|import)\b')
        if (($isEmpty -or $isComment) -and -not ($isEmpty -and $isNextCode)) {
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
function Apply-SurgicalRemovalsForFile {
    param([Parameter(Mandatory=$true)][string]$FilePath, [Parameter(Mandatory=$true)][System.Collections.Generic.List[object]]$FlaggedItems)
    if (-not (Test-Path -LiteralPath $FilePath)) { return }
    $ext = [IO.Path]::GetExtension($FilePath).ToLowerInvariant()
    $lines = Get-Content -LiteralPath $FilePath
    $removeLineIndexes = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($it in $FlaggedItems) { if ($it.IsEmpty -or $it.IsFullLineComment) { [void]$removeLineIndexes.Add([int]$it.LineIndexZeroBased) } }
    $newLines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($removeLineIndexes.Contains($i)) { continue }
        $line = $lines[$i]
        $inlineItems = @($FlaggedItems | Where-Object { $_.LineIndexZeroBased -eq $i -and -not $_.IsEmpty -and -not $_.IsFullLineComment })
        if ($inlineItems.Count -gt 0) {
            $hasBlock = $false; $hasHash = $false; $hashIndex = -1
            foreach ($it in $inlineItems) {
                if ($it.HasBlockInlineComment) { $hasBlock = $true }
                if ($it.HasHashInlineComment) { $hasHash = $true; $hashIndex = $it.HashCommentIndex }
            }
            if ($hasBlock -and ($line -match '/\*.*\*/')) { $line = [regex]::Replace($line, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) }
            if ($hasHash -and $ext -eq '.py' -and $hashIndex -ge 0) { $line = $line.Substring(0, $hashIndex).TrimEnd() }
            $line = $line.TrimEnd()
        }
        $newLines.Add($line)
    }
    Set-Content -LiteralPath $FilePath -Value ($newLines -join "`n") -Encoding UTF8
}
Set-Location $PSScriptRoot
$excludedDirNames = @('binaries','config','logs','mp3','!!!_Ouput_Video_Files_!!!','venv','.git')
$selfName = Split-Path -Leaf $PSCommandPath
$excludedFileNames = @('app.py','Installer.ps1','project_structure.txt', $selfName)
$allowedExtensions = @('.py','.txt','.md','.json','.ini','.cfg','.yml','.yaml','.js','.ts','.html','.css','.qss','.bat','.cmd','.ps1')
Write-Host "--- PHASE 1: Syntax & Indentation Check ---`n" -ForegroundColor Cyan
$pyFilesToCheck = Get-ChildItem -Recurse -Filter "*.py" | Where-Object {
    $dirParts = $_.DirectoryName -split '[\\/]'; foreach ($d in $excludedDirNames) { if ($dirParts -contains $d) { return $false } }; return $true
}
foreach ($file in $pyFilesToCheck) { [void](Test-PythonSyntax -FilePath $file.FullName) }
Write-Host "`n--- PHASE 2: Comment & Empty Line Identification ---`n" -ForegroundColor Cyan
$allFlagged = New-Object System.Collections.Generic.List[object]
$targetFiles = Get-ChildItem -Recurse -File | Where-Object {
    if ($excludedFileNames -contains $_.Name) { return $false }
    if ($allowedExtensions -notcontains $_.Extension.ToLowerInvariant()) { return $false }
    $dirParts = $_.DirectoryName -split '[\\/]'; foreach ($d in $excludedDirNames) { if ($dirParts -contains $d) { return $false } }; return $true
}
foreach ($file in $targetFiles) {
    $items = Get-FlaggedItemsForFile -FilePath $file.FullName
    foreach ($it in $items) {
        Write-Host "$($it.FilePath):$($it.LineNumberOneBased) [$($it.Kind)] " -NoNewline
        if ($it.IsEmpty -or $it.IsFullLineComment) { Write-Host $it.OriginalLine -ForegroundColor Red }
        else {
            if ($it.HasHashInlineComment -and $it.HashCommentIndex -ge 0) {
                Write-Host $it.OriginalLine.Substring(0, $it.HashCommentIndex) -NoNewline
                Write-Host $it.OriginalLine.Substring($it.HashCommentIndex) -ForegroundColor Red
            } else { Write-Host $it.OriginalLine -ForegroundColor Red }
        }
        $allFlagged.Add($it)
    }
}
if ($allFlagged.Count -gt 0) {
    Write-Host "`n" + ("-" * 120)
    $answer = (Read-Host 'Do you wish me to go ahead and perform these replacements now for you? (Y/N)').Trim()
    if ($answer -match '^(Y|YES)$') {
        $allFlagged | Group-Object -Property FilePath | ForEach-Object {
            $itemsList = New-Object System.Collections.Generic.List[object]
            foreach ($x in $_.Group) { $itemsList.Add($x) }
            Apply-SurgicalRemovalsForFile -FilePath $_.Name -FlaggedItems $itemsList
        }
        Write-Host "Done. Replacements applied." -ForegroundColor Green
    } else { Write-Host "No changes were made." -ForegroundColor Yellow }
} else { Write-Host "`nNothing to clean." -ForegroundColor Green }
pause
