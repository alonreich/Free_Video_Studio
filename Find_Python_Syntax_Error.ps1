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
            in_brackets_before = (bracket_level > 0)
            if not in_brackets_before:
                if current_indent % 4 != 0:
                    print(f"ALIGNMENT ERROR: Line {lnum} has {current_indent} spaces (not a multiple of 4).")
                    return False
                if current_indent > last_logical_indent + 4:
                    print(f"DEEP INDENT ERROR: Line {lnum} jumped from {last_logical_indent} to {current_indent} spaces.")
                    return False
                last_logical_indent = current_indent
            clean_line = re.sub(r"'.*?'|\".*?\"", "", line)
            bracket_level += clean_line.count('(') + clean_line.count('[') + clean_line.count('{')
            bracket_level -= clean_line.count(')') + clean_line.count(']') + clean_line.count('}')
        compile("".join(lines), filepath, 'exec')
        return True
    except SyntaxError as e:
        print(f"SYNTAX ERROR: {e.msg} | Line: {e.lineno}")
        return False
    except IndentationError as e:
        print(f"INDENTATION ERROR: {e.msg} | Line: {e.lineno}")
        return False
    except Exception as e:
        print(f"OTHER ERROR: {str(e)}")
        return False
if __name__ == '__main__':
    sys.exit(0 if check_indentation(sys.argv[1]) else 1)
"@
    $result = $checkScript | python - $FilePath 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[INVALID] $FilePath" -ForegroundColor Red
        if ($result) {
            Write-Host "    $result" -ForegroundColor Yellow
        }
        return $false
    }
    return $true
}
Set-Location $PSScriptRoot
$excludedDirNames = @('binaries','config','logs','mp3','!!!_Ouput_Video_Files_!!!','venv','.git')
$pyFiles = Get-ChildItem -Recurse -Filter "*.py" | Where-Object {
    $dirParts = $_.DirectoryName -split '[\\/]'
    foreach ($d in $excludedDirNames) {
        if ($dirParts -contains $d) { return $false }
    }
    return $true
}
Write-Host "Scanning $($pyFiles.Count) Python files for syntax and indentation errors...`n" -ForegroundColor Cyan
$errorCount = 0
foreach ($file in $pyFiles) {
    if (-not (Test-PythonSyntax -FilePath $file.FullName)) {
        $errorCount++
    }
}
Write-Host "`nScan Complete." -ForegroundColor Cyan
if ($errorCount -eq 0) {
    Write-Host "No syntax errors found. Your code is clean." -ForegroundColor Green
} else {
    Write-Host "Found $errorCount file(s) with errors. Fix them." -ForegroundColor Red
}
pause
