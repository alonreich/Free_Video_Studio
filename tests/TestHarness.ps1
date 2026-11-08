# TESTGATE_01 / TESTCLEAN_01. docs/README.md#GOV-AVGATE, 05#SYS-STORAGE-TABLE.
# Definitions only: importing this file never launches a suite or deletes output.
function Get-TestOutputRoots([string]$RepositoryRoot) {
    foreach ($relative in @('TestResults', 'artifacts\code-export-tests', 'tests\AvRegression', 'tests\MediaPipelineChecks\artifacts',
        'tests\FreeVideoStudio.Core.Tests\TestResults', 'tests\FreeVideoStudio.App.Tests\TestResults',
        'tests\FreeVideoStudio.AvRegression.Tests\TestResults', 'tests\FvsVerify.Tests\TestResults')) {
        [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relative))
    }
}

function Assert-TestOutputPath([string]$RepositoryRoot, [string]$Path) {
    $repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'FreeVideoStudio.sln') -PathType Leaf)) {
        throw 'Cleanup requires a verified repository root.'
    }
    if ($Path -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Cleanup refuses traversal.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $allowed = @(Get-TestOutputRoots $repo | Where-Object {
        $full.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($allowed.Count -eq 0) { throw "Not a test-output directory: $full" }
    # A root/ancestor junction would redirect even an apparently safe absolute path.
    $parent = [IO.DirectoryInfo]::new($full)
    while ($null -ne $parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Cleanup refuses a linked output root/ancestor: $($parent.FullName)"
        }
        $parent = $parent.Parent
    }
    return $full
}

function Remove-TestOutput([string]$RepositoryRoot, [string]$Path) {
    $full = Assert-TestOutputPath $RepositoryRoot $Path
    if (-not (Test-Path -LiteralPath $full)) { return }
    function Remove-OwnedTree([IO.DirectoryInfo]$Directory) {
        foreach ($entry in $Directory.GetFileSystemInfos()) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                # Delete the link itself, never enumerate its target. DevSandbox deliberately
                # creates junction fixtures; recursive Remove-Item must never follow those.
                if ($entry.Attributes -band [IO.FileAttributes]::Directory) { [IO.Directory]::Delete($entry.FullName) }
                else { Remove-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop }
            } elseif ($entry -is [IO.DirectoryInfo]) { Remove-OwnedTree $entry }
            else { Remove-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop }
        }
        [IO.Directory]::Delete($Directory.FullName)
    }
    Remove-OwnedTree ([IO.DirectoryInfo]::new($full))
}

function Enter-TestRun([string]$RepositoryRoot) {
    # Outside cleaned roots. FileShare.None prevents a second runner from erasing a live run.
    $lockPath = Join-Path $RepositoryRoot 'tests\.test-run.lock'
    try { return [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another test run owns the output folders, or the test lock cannot be opened.' }
}

function Reset-TestOutputs([string]$RepositoryRoot) {
    $paths = @(Get-TestOutputRoots $RepositoryRoot)
    # Validate ALL destinations before the first deletion.
    foreach ($path in $paths) { $null = Assert-TestOutputPath $RepositoryRoot $path }
    foreach ($path in $paths) { Remove-TestOutput $RepositoryRoot $path }
}

function New-TestFailure([string]$Suite, [string]$Message) {
    [pscustomobject]@{ Suite = $Suite; Name = '(runner)'; Outcome = 'Failed'; Message = $Message; Where = '' }
}

function Read-TestResults([string]$Path, [string]$Suite, [int]$ExitCode,
                          [string[]]$RequiredScenarios = @()) {
    $records = @()
    if ($ExitCode -ne 0) { $records += New-TestFailure $Suite "test process exited $ExitCode; see tests\AvRegression\last-run.log" }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @($records) + @(New-TestFailure $Suite 'Missing test results; suite did not complete.')
    }
    try {
        # No DTD/entity expansion, even for an unexpected/corrupt result file.
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) }
        finally { $reader.Dispose() }
        $summary = $xml.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]')
        $rows = @($xml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
        if ($null -eq $summary -or $summary.outcome -notin @('Completed', 'Passed')) {
            $records += New-TestFailure $Suite 'Missing or unsuccessful run-level result summary.'
        }
        if ($rows.Count -eq 0) { $records += New-TestFailure $Suite 'Zero test results; an empty run is not a pass.' }
        $counters = if ($summary) { $summary.SelectSingleNode('*[local-name()="Counters"]') } else { $null }
        if ($null -eq $counters -or [int]$counters.total -ne $rows.Count -or
            [int]$counters.executed -ne $rows.Count -or [int]$counters.passed -ne $rows.Count) {
            $records += New-TestFailure $Suite 'Incomplete, skipped or failed tests in run counters.'
        }
        $errors = $xml.SelectNodes('//*[local-name()="RunInfo"][@outcome="Error" or @outcome="Failed" or @outcome="Aborted"]')
        if ($errors.Count -gt 0) { $records += New-TestFailure $Suite 'Test host reported a run-level error.' }
        foreach ($row in $rows) {
            $errorInfo = $row.SelectSingleNode('*[local-name()="Output"]/*[local-name()="ErrorInfo"]')
            $message = ''; $where = ''
            if ($errorInfo) {
                $message = ([string]$errorInfo.Message -replace '^.*AvDefect : ', '' -replace '\s+', ' ').Trim()
                $match = [regex]::Match([string]$errorInfo.StackTrace, '(tests[\\/][^:]+\.cs):line (\d+)')
                if ($match.Success) { $where = "$($match.Groups[1].Value):$($match.Groups[2].Value)" }
            }
            $records += [pscustomobject]@{ Suite = $Suite; Name = [string]$row.testName;
                Outcome = [string]$row.outcome; Message = $message; Where = $where }
        }
        foreach ($scenario in $RequiredScenarios) {
            if (-not @($rows | Where-Object { $_.testName -match "\b${scenario}_" -and $_.outcome -eq 'Passed' }).Count) {
                $records += New-TestFailure $Suite "Required scenario $scenario did not pass."
            }
        }
    } catch { $records += New-TestFailure $Suite 'Malformed or unreadable test results.' }
    return $records
}
