param([ValidateSet('', 'run', 'build', 'rebuild', 'fresh', 'trace', 'clean', 'restore')][string]$Mode = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevSandbox.ps1')

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE). No app was launched." }
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

    $repoPrefix = $repo.TrimEnd('\') + '\'
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -in @('FreeVideoStudio.exe', 'mpv.exe', 'ffmpeg.exe', 'ffprobe.exe')
    })
    if ($running.Count) { throw 'Close the development app and its media tools normally, then run dev.cmd again. No process was stopped.' }

    $null = Assert-DevOwnedPath $output $repo
    $null = Assert-DevOwnedPath $data $devHome
    if ($Mode -eq 'restore') {
        Invoke-Dotnet @('restore', $project, '-r', 'win-x64')
        exit 0
    }
    if ($Mode -in @('clean', 'fresh', 'rebuild')) { Remove-DevCache $output $repo }
    if ($Mode -eq 'clean') { exit 0 }
    Clear-DevManagedOutput $app $output

    Initialize-DevSandbox $data (Join-Path $oldTemp '.dev_data') (Join-Path $devHome 'keep') (Join-Path $oldTemp 'media')
    Clear-DevRuntimeCaches $temporaryData
    $helperTemp = New-DevHelperScratch $helperTemp $helperHome
    if ($Mode -eq 'fresh') { Write-Host '[DEV] Fresh code and caches; all saved preferences are preserved.' }

    Invoke-Dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64',
        '--artifacts-path', (Join-Path $output 'build'), '-o', $app,
        '-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:SelfContained=true',
        '-p:UseSharedCompilation=false', '-nodeReuse:false')
    Invoke-Dotnet @('run', '--project', (Join-Path $repo 'build\FvsBuild\FvsBuild.csproj'),
        '--', '--dev-runtime')
    $exe = Join-Path $app 'FreeVideoStudio.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Published executable is missing. No app was launched.' }
    [IO.File]::WriteAllText((Join-Path $app '.fvs-development'), 'DEVISOLATION_01')
    if ($Mode -eq 'build') { Write-Host "[DEV] Current source staged: $exe"; exit 0 }

    $env:FVS_PROGRAMDATA_ROOT = $data
    $env:FVS_SYSTEM_TEMP_ROOT = $systemTemp
    $env:FVS_DEV_HELPER_TEMP = $helperTemp
    $env:FVS_DEV_SHARED_MEDIA = '1'
    $env:FVS_DEV_HOME = $devHome
    $env:FVS_DEV_UPDATE_SOURCE = $null
    $env:FVS_ALLOW_UNSIGNED_UPDATE = $null
    $env:FVS_DEV_LOG_DIR = Join-Path $temporaryData 'logs'
    if ($Mode -eq 'trace') {
        $env:FVS_DEV_LOG_DIR = Join-Path $repo '.devlogs'
        Remove-DevCache $env:FVS_DEV_LOG_DIR $repo
    }
    $env:TEMP = $helperTemp
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP, $env:FVS_DEV_LOG_DIR -Force | Out-Null
    Write-Host "[DEV] Current source, Release runtime. Preferences: $temporaryData; profiles/recovery: $data"
    $process = Start-Process -FilePath $exe -ArgumentList 'run-ui' -WorkingDirectory $app -WindowStyle Normal -PassThru -Wait
    exit $process.ExitCode
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
finally {
    if ($launchLock) { $launchLock.Dispose() }
}
