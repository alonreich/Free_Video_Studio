Set-Location $PSScriptRoot
$excludedDirNames = @('binaries','config','logs','mp3','!!!_Ouput_Video_Files_!!!')

$selfName = Split-Path -Leaf $PSCommandPath

$excludedFileNames = @('app.py','Installer.ps1','project_structure.txt', $selfName)

$allowedExtensions = @('.py','.txt','.md','.json','.ini','.cfg','.yml','.yaml','.js','.ts','.html','.css','.qss','.bat','.cmd','.ps1')

function Get-TargetFiles {
    Get-ChildItem -Recurse -File | Where-Object {
        if ($excludedFileNames -contains $_.Name) { return $false }
        if ($allowedExtensions -notcontains $_.Extension.ToLowerInvariant()) { return $false }
        $dirParts = $_.DirectoryName -split '[\\/]'
        foreach ($d in $excludedDirNames) {
            if ($dirParts -contains $d) { return $false }
        }
        return $true
    }
}
function Get-PythonCommentIndex {
    param(
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$Line,
        [Parameter(Mandatory=$true)][ref]$InTripleSingle,
        [Parameter(Mandatory=$true)][ref]$InTripleDouble
    )
    if ([string]::IsNullOrEmpty($Line)) { return -1 }
    $inSingle = $false
    $inDouble = $false
    $escape = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $ch = $Line[$i]
        if ($escape) { $escape = $false; continue }
        if ($InTripleSingle.Value) {
            if ($i -le $Line.Length - 3 -and $Line.Substring($i,3) -eq "'''") {
                $InTripleSingle.Value = $false
                $i += 2
            }
            continue
        }
        if ($InTripleDouble.Value) {
            if ($i -le $Line.Length - 3 -and $Line.Substring($i,3) -eq '"""') {
                $InTripleDouble.Value = $false
                $i += 2
            }
            continue
        }
        if (($inSingle -or $inDouble) -and $ch -eq '\') {
            $escape = $true
            continue
        }
        if (-not ($inSingle -or $inDouble)) {
            if ($i -le $Line.Length - 3) {
                $tri = $Line.Substring($i,3)
                if ($tri -eq "'''") { $InTripleSingle.Value = $true; $i += 2; continue }
                if ($tri -eq '"""') { $InTripleDouble.Value = $true; $i += 2; continue }
            }
        }
        if (-not $inDouble -and $ch -eq "'") { $inSingle = -not $inSingle; continue }
        if (-not $inSingle -and $ch -eq '"') { $inDouble = -not $inDouble; continue }
        if (-not ($inSingle -or $inDouble)) {
            if ($ch -eq '#') { return $i }
        }
    }
    return -1
}
function Get-FlaggedItemsForFile {
    param([Parameter(Mandatory=$true)][string]$FilePath)
    $ext = [IO.Path]::GetExtension($FilePath).ToLowerInvariant()
    $lines = Get-Content -LiteralPath $FilePath
    $items = New-Object System.Collections.Generic.List[object]
	$inTripleSingle = $false
	$inTripleDouble = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $cur = $lines[$i]
        $next = if ($i -lt $lines.Count - 1) { $lines[$i+1] } else { '' }
        $isEmpty = ($cur -match '^\s*$')
        $isFullLineComment = (($cur -match '^\s*\#') -or ($cur -match '^\s*\*\*') -or ($cur -match '^\s*//') -or ($cur -match '^\s*;') -or ($cur -match '^\s*/\*') -or ($cur -match '^\s*\*/') -or ($cur -match '^\s*\*'))
        $hasBlockInlineComment = ($cur -match '/\*.*\*/')
		$hashIndex = -1
		$hasHashInlineComment = $false
		if ($ext -eq '.py' -and -not $isEmpty) {
			$hashIndex = Get-PythonCommentIndex -Line $cur -InTripleSingle ([ref]$inTripleSingle) -InTripleDouble ([ref]$inTripleDouble)
			$hasHashInlineComment = ($hashIndex -ge 0)
		}
        $isComment = ($isFullLineComment -or $hasBlockInlineComment -or $hasHashInlineComment)
        $isNextCode = ($next -match '^\s*(def|class|from|import)\b')
        $preserve = ($isEmpty -and $isNextCode)
        if (($isEmpty -or $isComment) -and -not $preserve) {
            $kind = if ($isEmpty) { 'EMPTY' } else { 'COMMENT' }
            $items.Add([pscustomobject]@{
                FilePath=$FilePath
                LineIndexZeroBased=$i
                LineNumberOneBased=($i+1)
                Kind=$kind
                IsEmpty=$isEmpty
                IsFullLineComment=$isFullLineComment
                HasBlockInlineComment=$hasBlockInlineComment
                HasHashInlineComment=$hasHashInlineComment
                HashCommentIndex=$hashIndex
                OriginalLine=$cur
            })
        }
    }
    return $items
}
function Apply-SurgicalRemovalsForFile {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][System.Collections.Generic.List[object]]$FlaggedItems
    )
    if (-not (Test-Path -LiteralPath $FilePath)) { return }
    $ext = [IO.Path]::GetExtension($FilePath).ToLowerInvariant()
    $lines = Get-Content -LiteralPath $FilePath
    $removeLineIndexes = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($it in $FlaggedItems) {
        if ($it.IsEmpty -or $it.IsFullLineComment) {
            [void]$removeLineIndexes.Add([int]$it.LineIndexZeroBased)
        }
    }
    $newLines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($removeLineIndexes.Contains($i)) { continue }
        $line = $lines[$i]
        $inlineItems = @($FlaggedItems | Where-Object { $_.LineIndexZeroBased -eq $i -and -not $_.IsEmpty -and -not $_.IsFullLineComment })
        if ($inlineItems.Count -gt 0) {
            $hasBlock = $false
            $hasHash = $false
            $hashIndex = -1
            foreach ($it in $inlineItems) {
                if ($it.HasBlockInlineComment) { $hasBlock = $true }
                if ($it.HasHashInlineComment) { $hasHash = $true; $hashIndex = $it.HashCommentIndex }
            }
            if ($hasBlock -and ($line -match '/\*.*\*/')) {
                $line = [regex]::Replace($line, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            }
            if ($hasHash -and $ext -eq '.py' -and $hashIndex -ge 0) {
                $line = $line.Substring(0, $hashIndex).TrimEnd()
            }
            $line = $line.TrimEnd()
        }
        $newLines.Add($line)
    }
    $oldText = ($lines -join "`n")
    $newText = ($newLines.ToArray() -join "`n")
    if ($newText -ne $oldText) {
        Set-Content -LiteralPath $FilePath -Value $newText -Encoding UTF8
    }
}
Write-Output 'Instructions for an AI Agent: Below are result of comments and empty lines which should be removed from the code which is inside the files. when removing these below comments, or empty lines, please becareful not to touch or hurt the code. only remove the comment and empty line surgecly, without impacting the code:'
$allFlagged = New-Object System.Collections.Generic.List[object]
Get-TargetFiles | ForEach-Object {
    $file = $_.FullName
    $items = Get-FlaggedItemsForFile -FilePath $file
    foreach ($it in $items) {
        $cur = $it.OriginalLine
        $prefix = "{0}:{1} [{2}] " -f $it.FilePath, $it.LineNumberOneBased, $it.Kind
        Write-Host $prefix -NoNewline
        if ($it.IsEmpty -or $it.IsFullLineComment) {
            Write-Host $cur -ForegroundColor Red
        } else {
            if ($it.HasHashInlineComment -and $it.HashCommentIndex -ge 0) {
                $codePart = $cur.Substring(0, $it.HashCommentIndex)
                $commentPart = $cur.Substring($it.HashCommentIndex)
                Write-Host $codePart -NoNewline
                Write-Host $commentPart -ForegroundColor Red
            }
            elseif ($it.HasBlockInlineComment) {
                $match = [regex]::Match($cur, '/\*.*?\*/')
                if ($match.Success) {
                    $pre = $cur.Substring(0, $match.Index)
                    $comment = $match.Value
                    $post = $cur.Substring($match.Index + $match.Length)
                    Write-Host $pre -NoNewline
                    Write-Host $comment -NoNewline -ForegroundColor Red
                    Write-Host $post
                } else {
                    Write-Host $cur -ForegroundColor Red
                }
            } else {
                Write-Host $cur -ForegroundColor Red
            }
        }

        $allFlagged.Add($it)
    }
}

Write-Output ""
Write-Output ""
Write-Output ""
Write-Output "-------------------------------------------------------------------------------------------------------------------------------"
Write-Output "-------------------------------------------------------------------------------------------------------------------------------"
Write-Output ""
Write-Output ""
Write-Output ""

$answer = Read-Host 'Do you wish me to go a head and perform these replacements now for you? (Y/N)'
$answer = ($answer + '').Trim()

if ($answer -match '^(Y|YES)$') {
    $byFile = $allFlagged | Group-Object -Property FilePath
    foreach ($group in $byFile) {
        $filePath = $group.Name
        $itemsList = New-Object System.Collections.Generic.List[object]
        foreach ($x in $group.Group) { $itemsList.Add($x) }
        Apply-SurgicalRemovalsForFile -FilePath $filePath -FlaggedItems $itemsList
    }
    Write-Output "Done. Replacements applied to the flagged lines only."
} else {
    Write-Output "No changes were made."
}