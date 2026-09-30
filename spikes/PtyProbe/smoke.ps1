param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PackageDirectory = './artifacts/pty',
    [string]$ReportDirectory = './artifacts/pty-results',
    [string]$ExpectedRuntime = '',
    [string]$GlobalDirectory = ''
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$reports = [IO.Path]::GetFullPath($ReportDirectory)
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram PTY tools ' + [guid]::NewGuid().ToString('N'))
$cliRoot = if ($GlobalDirectory) { [IO.Path]::GetFullPath($GlobalDirectory) }
    else { Join-Path ([IO.Path]::GetTempPath()) ('mpty global ' + [guid]::NewGuid().ToString('N').Substring(0, 12)) }
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
$previousCliHome = $env:DOTNET_CLI_HOME
$previousPathSetup = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
try {
    $env:DOTNET_CLI_HOME = $cliRoot
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    Invoke-DotNet tool install --global Milligram.PtySpike --source $packageSource --version $Version
    $globalTool = Join-Path $cliRoot '.dotnet/tools' $(if ($IsWindows) { 'milligram-pty-spike.exe' } else { 'milligram-pty-spike' })
    if (!(Test-Path -LiteralPath $globalTool)) { throw 'Global installation did not produce the isolated tool shim' }
    foreach ($mode in @('async', 'blocking')) {
        [string[]]$modeArguments = @()
        if ($mode -eq 'blocking') { $modeArguments += '--blocking' }
        if ($ExpectedRuntime) { $modeArguments += @('--expected-rid', $ExpectedRuntime) }
        $report = Join-Path $reports "global-$mode.md"
        & $globalTool @modeArguments --output $report
        if ($LASTEXITCODE -ne 0) { throw "Global tool probe failed in $mode mode ($LASTEXITCODE)" }
        if (![IO.File]::ReadAllText($report).Contains("Async I/O: $($mode -eq 'async')")) {
            throw "Global probe did not receive the requested $mode mode: $report"
        }
    }
}
finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $previousPathSetup
}
Write-Output "Package $Version passed tool-path, global-tool and dnx probes in both I/O modes."
