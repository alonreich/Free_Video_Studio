param([string]$Command = '')
$ErrorActionPreference = 'Continue'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $repo
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

function Write-Blocked([string]$Title, [string[]]$Lines) {
    $bar = '#' * 75
    Write-Host ''
    Write-Host $bar -ForegroundColor Red
    Write-Host " $Title" -ForegroundColor Red
    foreach ($line in $Lines) { Write-Host "   $line" -ForegroundColor Red }
    Write-Host $bar -ForegroundColor Red
    Write-Host ''
}

function Get-RequiredSdkMajor {
    $files = @()
    try { $files = @(& git -C $repo ls-files -- '*.csproj' '*.props' 2>$null | ForEach-Object { Join-Path $repo $_ }) } catch { $files = @() }
    if ($files.Count -eq 0) {
        $files = @(Get-ChildItem -LiteralPath $repo -Recurse -File -Include '*.csproj', '*.props' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|\.dev-run|\.git)\\' } | ForEach-Object { $_.FullName })
    }
    $major = 0
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $text = [IO.File]::ReadAllText($file)
        foreach ($match in [regex]::Matches($text, '<TargetFrameworks?>([^<]+)<')) {
            foreach ($tfm in [regex]::Matches($match.Groups[1].Value, 'net(\d+)\.\d')) {
                $value = [int]$tfm.Groups[1].Value
                if ($value -gt $major) { $major = $value }
            }
        }
    }
    if ($major -eq 0) { $major = 8 }
    return $major
}

function Test-Sdk([string]$Dotnet, [int]$Major) {
    if (-not $Dotnet -or -not (Test-Path -LiteralPath $Dotnet)) { return $false }
    $output = @(& $Dotnet --version 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) { return $false }
    $version = ([string]$output[0]).Trim()
    return ([int](($version -split '\.')[0])) -ge $Major
}

function Use-Dotnet([string]$Folder) {
    $env:DOTNET_ROOT = $Folder
    $env:PATH = $Folder + ';' + $env:PATH
}

function Install-LocalSdk([int]$Major) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('dotnet-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $script = Join-Path $work 'dotnet-install.ps1'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host "[SDK] Downloading Microsoft's dotnet-install script to install the .NET $Major SDK for this user (no admin)..."
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing -ErrorAction Stop
        $signature = Get-AuthenticodeSignature -LiteralPath $script
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            Write-Blocked 'BLOCKED: the downloaded .NET installer is not validly signed by Microsoft' @('It was NOT run.', "Fix: winget install Microsoft.DotNet.SDK.$Major")
            return $false
        }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel "$Major.0" -InstallDir $localDotnet -NoPath
        return $LASTEXITCODE -eq 0
    }
    catch {
        Write-Blocked "BLOCKED: the .NET $Major SDK could not be installed automatically" @($_.Exception.Message, "Fix: winget install Microsoft.DotNet.SDK.$Major")
        return $false
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Initialize-Sdk {
    $major = Get-RequiredSdkMajor
    $system = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($system -and (Test-Sdk $system.Source $major)) { return $true }
    $local = Join-Path $localDotnet 'dotnet.exe'
    if (Test-Sdk $local $major) { Use-Dotnet $localDotnet; Write-Host "[SDK] Using the .NET SDK in $localDotnet"; return $true }
    if (-not (Install-LocalSdk $major)) { return $false }
    if (Test-Sdk $local $major) { Use-Dotnet $localDotnet; Write-Host "[SDK] Installed the .NET $major SDK into $localDotnet"; return $true }
    Write-Blocked "BLOCKED: no .NET $major SDK (or newer) is available" @('This project needs it to build.', "Fix: winget install Microsoft.DotNet.SDK.$major")
    return $false
}

try {
    $config = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'app.json')) | ConvertFrom-Json
    if (-not (Initialize-Sdk)) { exit 1 }
    $orchestrator = Join-Path $repo $config.orchestrator
    switch ($Command.ToLowerInvariant()) {
        'dev' {
            if (-not $config.dev) {
                & dotnet run --project $orchestrator -c Release -- --dev-run
                exit $LASTEXITCODE
            }
            foreach ($step in @($config.dev)) {
                $parts = @($step.run | ForEach-Object { ([string]$_).Replace('{repo}', $repo) })
                $exe = $parts[0]
                $arguments = @($parts | Select-Object -Skip 1)
                & $exe @arguments
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }
            exit 0
        }
        'dev_build' {
            & dotnet run --project $orchestrator -c Release -- --dev
            exit $LASTEXITCODE
        }
        'build' {
            & dotnet run --project $orchestrator -c Release
            exit $LASTEXITCODE
        }
        default {
            Write-Blocked "BLOCKED: unknown command '$Command'" @('Use dev.cmd, dev_build.cmd or build.cmd.')
            exit 1
        }
    }
}
catch {
    Write-Blocked 'BLOCKED: the build launcher failed' @($_.Exception.Message)
    exit 1
}
