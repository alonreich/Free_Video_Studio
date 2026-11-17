param([switch]$NoPublish)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here '..\..')
$out = Join-Path $here 'out'
$venv = Join-Path $out 'venv'
$py = Join-Path $venv 'Scripts\python.exe'
$pack = Join-Path $out 'pack'
$ort = Join-Path $out 'ort'
$checkpoint = Join-Path $out 'sam2.1_hiera_small.pt'
$checkpointUrl = 'https://dl.fbaipublicfiles.com/segment_anything_2/092824/sam2.1_hiera_small.pt'
$tag = 'localai-v1'
$repoSlug = 'alonreich/Free_Video_Studio'
$files = @('onnxruntime.dll', 'DirectML.dll', 'sam2_small_encoder.onnx', 'sam2_small_decoder.onnx')

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed (exit $LASTEXITCODE)." }
}

New-Item -ItemType Directory -Force -Path $out, $pack | Out-Null

Step '1/5 Python environment (only inside developer_tools\local_ai_pack\out)'
if (-not (Test-Path $py)) { Run 'python' @('-m', 'venv', $venv) }
Run $py @('-m', 'pip', 'install', '-q', '--upgrade', 'pip')
Run $py @('-m', 'pip', 'install', '-q', 'torch', '--index-url', 'https://download.pytorch.org/whl/cpu')
Run $py @('-m', 'pip', 'install', '-q', 'onnx', 'onnxruntime', 'onnxscript', 'git+https://github.com/facebookresearch/sam2.git')

Step '2/5 SAM 2.1 Small checkpoint (Meta, Apache-2.0)'
if (-not (Test-Path $checkpoint)) { Invoke-WebRequest -Uri $checkpointUrl -OutFile $checkpoint }

Step '3/5 Export the two ONNX files'
$env:SAM2_BUILD_CUDA = '0'
Run $py @((Join-Path $here 'export_sam2_onnx.py'), '--checkpoint', $checkpoint, '--out', $pack)

Step '4/5 Microsoft runtime DLLs, pins and the test folder'
Run $py @((Join-Path $here 'build_pack.py'), '--pack', $pack, '--test-folder', $ort)
Write-Host "The A/V gate now runs the Local AI real-model tests with $pack."

if ($NoPublish) { Write-Host 'Not published (-NoPublish).'; exit 0 }

Step "5/5 Publish to the GitHub pre-release $tag"
Write-Host "This uploads $($files.Count) files (about 110 MB) to https://github.com/$repoSlug/releases/tag/$tag as a PRE-RELEASE."
Write-Host 'It is not an app release: the app update channel never picks up a pre-release.'
$answer = Read-Host 'Publish now? [Y/N]'
if ($answer -notmatch '^(y|yes)$') { Write-Host 'Not published. Run this again when you are ready.'; exit 2 }

& gh release view $tag --repo $repoSlug *> $null
if ($LASTEXITCODE -ne 0) {
    Run 'gh' @('release', 'create', $tag, '--repo', $repoSlug, '--title', 'Local AI pack v1', '--notes', 'Optional Local AI pack for AI Smart Zoom (not an app release).', '--prerelease')
}
$paths = $files | ForEach-Object { Join-Path $pack $_ }
Run 'gh' (@('release', 'upload', $tag, '--repo', $repoSlug, '--clobber') + $paths)

Step 'Checking every uploaded file downloads with the pinned size'
$bad = @()
foreach ($name in $files) {
    $url = "https://github.com/$repoSlug/releases/download/$tag/$name"
    $tmp = Join-Path $out ("check-" + $name)
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        $want = (Get-Item (Join-Path $pack $name)).Length
        $got = (Get-Item $tmp).Length
        $hashOk = (Get-FileHash $tmp -Algorithm SHA256).Hash -eq (Get-FileHash (Join-Path $pack $name) -Algorithm SHA256).Hash
        if ($got -ne $want -or -not $hashOk) { $bad += "$name (size $got, expected $want, hash ok: $hashOk)" }
    } catch { $bad += "$name ($($_.Exception.Message))" }
    finally { if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Force } }
}
if ($bad.Count -gt 0) {
    Write-Host 'NOT switched on. These files did not download correctly:' -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Run $py @((Join-Path $here 'build_pack.py'), '--pack', $pack, '--released', '--test-folder', $ort)
Write-Host ''
Write-Host 'Published and verified. LocalAiPackFiles.cs now says Released = true.' -ForegroundColor Green
Write-Host 'Next: build with dev_build.cmd, run tests\run-av-tests.cmd, then commit src\FreeVideoStudio.Core\LocalAi\LocalAiPackFiles.cs.'
exit 0
