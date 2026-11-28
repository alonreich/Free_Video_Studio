$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevSandbox.ps1')

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE). No app was launched." }
}

function Close-RepoProcesses([string]$RepoPrefix) {
    $names = @('FreeVideoStudio.exe', 'mpv.exe', 'ffmpeg.exe', 'ffprobe.exe')
    $owned = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($RepoPrefix, [StringComparison]::OrdinalIgnoreCase) -and $_.Name -in $names
    })
    if (-not $owned.Count) { return }
    foreach ($entry in $owned) {
        Write-Host "[DEV] Closing $($entry.Name) (PID $($entry.ProcessId)), started from this repository..."
        $process = Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue
        if ($process) { $null = $process.CloseMainWindow() }
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @($owned | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count) {
        Start-Sleep -Milliseconds 250
    }
    foreach ($entry in $owned) { Stop-Process -Id $entry.ProcessId -Force -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and @($owned | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count) {
        Start-Sleep -Milliseconds 250
    }
    if (@($owned | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count) {
        throw 'A development copy started from this repository could not be closed. No app was launched.'
    }
}

try {
    $repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    Set-Location -LiteralPath $repo
    $output = Join-Path $repo '.dev-run'
    $app = Join-Path $output 'app'
    $devHome = Join-Path $env:LOCALAPPDATA 'FreeVideoStudio_DEV'
    $data = Join-Path $devHome 'data'
    $systemTemp = if ($env:FVS_SYSTEM_TEMP_ROOT) { $env:FVS_SYSTEM_TEMP_ROOT } elseif ($env:TMP) { $env:TMP } else { $env:TEMP }
    $systemTemp = [IO.Path]::GetFullPath($systemTemp)
    $oldTemp = Join-Path $systemTemp 'FreeVideoStudio_DEV'
    $temporaryData = Join-Path $oldTemp 'data'
    $helperHome = Join-Path $systemTemp 'Local\FreeVideoStudio_DEV'
    $helperTemp = Join-Path $helperHome 'data\scratch\os'
    $project = Join-Path $repo 'src\FreeVideoStudio.App\FreeVideoStudio.App.csproj'
    $lockPath = Assert-DevOwnedPath (Join-Path $repo '.dev-run.lock') $repo
    $launchLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)

    Close-RepoProcesses ($repo.TrimEnd('\') + '\')

    $null = Assert-DevOwnedPath $output $repo
    $null = Assert-DevOwnedPath $data $devHome

    Initialize-DevSandbox $data (Join-Path $oldTemp '.dev_data') (Join-Path $devHome 'keep') (Join-Path $oldTemp 'media')
    Clear-DevRuntimeCaches $temporaryData
    $helperTemp = New-DevHelperScratch $helperTemp $helperHome

    $publish = {
        Clear-DevManagedOutput $app $output
        Invoke-Dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64',
            '--artifacts-path', (Join-Path $output 'build'), '-o', $app,
            '-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:SelfContained=true',
            '-p:UseSharedCompilation=false', '-nodeReuse:false')
        Invoke-Dotnet @('run', '--project', (Join-Path $repo 'build\FvsBuild\FvsBuild.csproj'),
            '--', '--dev-runtime')
    }
    try { & $publish }
    catch {
        Write-Host '[DEV] The incremental build failed. Retrying once from a clean .dev-run folder (settings are kept)...' -ForegroundColor Yellow
        Remove-DevCache $output $repo
        & $publish
    }
    $exe = Join-Path $app 'FreeVideoStudio.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Published executable is missing. No app was launched.' }
    [IO.File]::WriteAllText((Join-Path $app '.fvs-development'), 'DEVISOLATION_01')

    $env:FVS_PROGRAMDATA_ROOT = $data
    $env:FVS_SYSTEM_TEMP_ROOT = $systemTemp
    $env:FVS_DEV_HELPER_TEMP = $helperTemp
    $env:FVS_DEV_SHARED_MEDIA = '1'
    $env:FVS_DEV_HOME = $devHome
    $env:FVS_DEV_UPDATE_SOURCE = $null
    $env:FVS_ALLOW_UNSIGNED_UPDATE = $null
    $env:FVS_DEV_LOG_DIR = Join-Path $temporaryData 'logs'
    $env:TEMP = $helperTemp
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP, $env:FVS_DEV_LOG_DIR -Force | Out-Null
    Write-Host "[DEV] Current source, Release runtime. Preferences: $temporaryData; profiles/recovery: $data"
    $process = Start-Process -FilePath $exe -ArgumentList 'run-ui' -WorkingDirectory $app -WindowStyle Normal -PassThru -Wait
    exit $process.ExitCode
}
catch {
    Write-Host ''
    Write-Host ('#' * 75) -ForegroundColor Red
    Write-Host ' DEV RUN BLOCKED' -ForegroundColor Red
    Write-Host ('   ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('#' * 75) -ForegroundColor Red
    exit 1
}
finally {
    if ($launchLock) { $launchLock.Dispose() }
}
