param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ExpectedRuntime,
    [string]$PackageDirectory = './artifacts/pty'
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The long-path comparison is Windows-only' }
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$reports = Join-Path $PWD 'artifacts/pty-long-results'
$cliRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram PTY long path ' + ('x' * 90) + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$previousCliHome = $env:DOTNET_CLI_HOME
$previousPathSetup = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
$previousCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
try {
    $env:DOTNET_CLI_HOME = $cliRoot
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & dotnet tool install --global Milligram.PtySpike --source $packageSource --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Long-path installation failed' }
    $tool = Join-Path $cliRoot '.dotnet/tools/milligram-pty-spike.exe'
    $summary = @('# Windows long-path comparison', '', "LongPathsEnabled: $((Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').LongPathsEnabled)", '')
    foreach ($mode in @('async', 'blocking')) {
        [string[]]$modeArguments = @('--expected-rid', $ExpectedRuntime)
        if ($mode -eq 'blocking') { $modeArguments += '--blocking' }
        & $tool @modeArguments --output (Join-Path $reports "baseline-$mode.md")
        $baseline = $LASTEXITCODE
        & $tool @modeArguments --preload-long-path --output (Join-Path $reports "preloaded-$mode.md")
        $preload = $LASTEXITCODE
        $summary += "$mode`: baseline exit $baseline; extended-path preload exit $preload"
        [IO.File]::WriteAllLines((Join-Path $reports 'comparison.md'), $summary)
        if ($preload -ne 0) { throw "The extended-path preload did not fix $mode mode" }
    }
}
finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $previousPathSetup
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificate
}
