param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PackageDirectory = './artifacts/pty',
    [string]$ReportDirectory = './artifacts/pty-results',
    [string]$ExpectedRuntime = ''
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$reports = [IO.Path]::GetFullPath($ReportDirectory)
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram PTY tools ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reports -Force | Out-Null

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed ($LASTEXITCODE)" }
}

Invoke-DotNet tool install Milligram.PtySpike --tool-path $toolRoot --source $packageSource --version $Version
$tool = Join-Path $toolRoot $(if ($IsWindows) { 'milligram-pty-spike.exe' } else { 'milligram-pty-spike' })
foreach ($mode in @('async', 'blocking')) {
    [string[]]$modeArguments = @()
    if ($mode -eq 'blocking') { $modeArguments += '--blocking' }
    if ($ExpectedRuntime) { $modeArguments += @('--expected-rid', $ExpectedRuntime) }
    $installedReport = Join-Path $reports "installed-$mode.md"
    $dnxReport = Join-Path $reports "dnx-$mode.md"
    & $tool @modeArguments --output $installedReport
    if ($LASTEXITCODE -ne 0) { throw "Installed tool probe failed in $mode mode ($LASTEXITCODE)" }
    Invoke-DotNet dnx -y --source $packageSource "Milligram.PtySpike@$Version" -- @modeArguments --output $dnxReport
    foreach ($report in @($installedReport, $dnxReport)) {
        if (![IO.File]::ReadAllText($report).Contains("Async I/O: $($mode -eq 'async')")) {
            throw "Probe did not receive the requested $mode mode: $report"
        }
    }
}
Write-Output "Package $Version passed installed-tool and dnx probes in both I/O modes."
