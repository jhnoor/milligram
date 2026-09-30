param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ExpectedRuntime,
    [string]$PackageDirectory = './artifacts/pty',
    [ValidateSet('boundary', 'long')][string]$Layout = 'boundary'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$IsWindows) { throw 'The long-path comparison is Windows-only' }
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$reports = Join-Path $PWD "artifacts/pty-long-results/$Layout"
$cliRoot = Join-Path ([IO.Path]::GetTempPath()) ('mpty global ' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$nativeRelative = ".dotnet/tools/.store/milligram.ptyspike/$Version/milligram.ptyspike/$Version/tools/net10.0/any/runtimes/$ExpectedRuntime/native/conpty.dll"
$targetLength = if ($Layout -eq 'long') { 330 } elseif ($ExpectedRuntime -eq 'win-arm64') { 259 } else { 257 }
$padding = $targetLength - (Join-Path $cliRoot $nativeRelative).Length
if ($padding -lt 0) { throw 'The temporary root is too long for the requested boundary comparison' }
$cliRoot += 'x' * $padding
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
    if (!(Test-Path -LiteralPath (Join-Path $cliRoot $nativeRelative))) { throw 'Expected native package layout was not installed' }
    $tool = Join-Path $cliRoot '.dotnet/tools/milligram-pty-spike.exe'
    $summary = @('# Windows long-path comparison', '', "Layout: $Layout; DLL path length: $targetLength",
        "LongPathsEnabled: $((Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').LongPathsEnabled)", '')
    foreach ($mode in @('async', 'blocking')) {
        [string[]]$modeArguments = @('--expected-rid', $ExpectedRuntime)
        if ($mode -eq 'blocking') { $modeArguments += '--blocking' }
        & $tool @modeArguments --output (Join-Path $reports "baseline-$mode.md")
        $baseline = $LASTEXITCODE
        & $tool @modeArguments --initialize-backend --output (Join-Path $reports "initialized-$mode.md")
        $initialized = $LASTEXITCODE
        & $tool @modeArguments --preload-long-path --output (Join-Path $reports "preloaded-$mode.md")
        $preload = $LASTEXITCODE
        $summary += "$mode`: baseline exit $baseline; early initialization exit $initialized; extended-path preload exit $preload"
        [IO.File]::WriteAllLines((Join-Path $reports 'comparison.md'), $summary)
        if ($preload -ne 0) { throw "The extended-path preload did not fix $mode mode" }
        if ($Layout -eq 'boundary') {
            $controlReport = [IO.File]::ReadAllText((Join-Path $reports "baseline-$mode.md"))
            if ($baseline -eq 0 -or !$controlReport.Contains('DllNotFoundException') -or !$controlReport.Contains('0x800700CE')) {
                throw "The $mode boundary control did not reproduce the native loader failure; review the comparison"
            }
        }
    }
}
finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $previousPathSetup
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificate
}
