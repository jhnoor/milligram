param(
    [Parameter(Mandatory)][string]$ToolAssembly,
    [Parameter(Mandatory)][string]$MsBuildPath,
    [string]$EvidenceDirectory = 'artifacts/legacy-metrics'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$IsWindows) { throw 'The legacy metrics fixture requires Windows and full Visual Studio MSBuild.' }
$ToolAssembly = [IO.Path]::GetFullPath($ToolAssembly)
$MsBuildPath = [IO.Path]::GetFullPath($MsBuildPath)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) ('mg-polly-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$pin = '1a3bf7bf33cfeccce2e224f29cb273e7d333528c'
$packages = [ordered]@{}
$result = [ordered]@{ commit = $pin; sample = $sampleRoot; os = [Environment]::OSVersion.VersionString; status = 'running' }
$previousTelemetry = $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT
$env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = '1'

function Invoke-Legacy([string]$Name, [string]$Command, [string[]]$Arguments, [int[]]$Expected = @(0)) {
    $lines = @(& $Command @Arguments 2>&1)
    $code = $LASTEXITCODE
    $text = ($lines | ForEach-Object { $_.ToString() }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $evidence "$Name.log"), $text)
    Write-Host ($lines | Select-Object -Last 15 | Out-String)
    if ($Expected -notcontains $code) { throw "$Name exited $code; see $evidence/$Name.log. Sample: $sampleRoot" }
    return $code
}

function Get-LegacyPackage([string]$Id, [string]$Version, [string]$Destination, [string]$ExpectedHash = '') {
    $lower = $Id.ToLowerInvariant()
    $archive = Join-Path $sampleRoot ('.milligram/packages/' + $lower + '.' + $Version + '.nupkg')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive)) | Out-Null
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$lower/$Version/$lower.$Version.nupkg" -OutFile $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($ExpectedHash -and $hash -ne $ExpectedHash) { throw "Unexpected package content: $Id $Version" }
    $packages[$Id + '/' + $Version] = $hash
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $Destination)
}

function Build-Legacy([string]$Name) {
    [void](Invoke-Legacy $Name $MsBuildPath @($testProject, '/nologo', '/m:1', '/p:Configuration=Release', '/p:GitVersion_NoFetchEnabled=true'))
}

function Collect-Legacy([string]$Name, [string[]]$Filter = @(), [int]$Failures = 0) {
    $report = Join-Path $evidence "$Name.cobertura.xml"
    $testReport = Join-Path $evidence "$Name.tests.xml"
    $code = Invoke-Legacy $Name $collector (@('collect', '-f', 'cobertura', '-o', $report, '-s', $settings, '--',
        $runner, $testAssembly, '-noshadow', '-parallel', 'none', '-xml', $testReport) + $Filter) @(0, 1)
    [xml]$tests = Get-Content -LiteralPath $testReport -Raw
    $assembly = $tests.assemblies.assembly
    if ([int]$assembly.total -lt 1 -or [int]$assembly.failed -ne $Failures -or [int]$assembly.errors -ne 0) { throw "Unexpected test outcomes: $Name" }
    if (($Failures -eq 0 -and $code -ne 0) -or ($Failures -gt 0 -and $code -eq 0)) { throw 'The collector lost the test runner exit status.' }
    [xml]$coverage = Get-Content -LiteralPath $report -Raw
    $classes = @($coverage.coverage.packages.package.classes.class)
    if ($classes.Count -eq 0) { throw "$Name did not instrument any source." }
    $result[$Name] = @{ total = [int]$assembly.total; passed = [int]$assembly.passed; failed = [int]$assembly.failed; exitCode = $code; classes = $classes.Count }
    return $report
}

function Assert-ImportedCoverage([string]$Report, [string]$Name) {
    [void](Invoke-Legacy "import-$Name" 'dotnet' @($ToolAssembly, 'crap', '--coverage', $Report, '--project', $sampleRoot))
    $model = Get-Content -LiteralPath (Join-Path $sampleRoot '.milligram/model.json') -Raw | ConvertFrom-Json -AsHashtable
    $snapshot = Get-Content -LiteralPath (Join-Path $sampleRoot '.milligram/metrics/crap.json') -Raw | ConvertFrom-Json -AsHashtable
    $type = @($model.types | Where-Object id -eq 'Polly.Context')
    if ($type.Count -ne 1 -or !$type[0].context.resolved -or $type[0].context.configuration -ne 'Release') { throw 'The real Framework context was not resolved.' }
    foreach ($expected in @(@('ExecutionKey', 1.0), @('ExecutionGuid', 0.0))) {
        $member = @($type[0].members | Where-Object name -eq $expected[0])
        if ($member.Count -ne 1) { throw "Missing real source member: $($expected[0])" }
        $entry = $snapshot.members[$member[0].id]
        if (!$entry -or $entry.coverage -ne $expected[1] -or $entry.hash -ne $member[0].hash) { throw "Wrong or stale imported coverage: $($expected[0])" }
        if ($member[0].span.file -ne 'src/Polly.Shared/Context.cs') { throw 'Coverage lost the shared source path.' }
    }
    Copy-Item -LiteralPath (Join-Path $sampleRoot '.milligram/metrics/crap.json') -Destination (Join-Path $evidence "$Name.crap.json")
}

try {
    [void](Invoke-Legacy 'clone' 'git' @('clone', '--quiet', '--no-checkout', 'https://github.com/App-vNext/Polly.git', $sampleRoot))
    [void](Invoke-Legacy 'checkout' 'git' @('-C', $sampleRoot, 'checkout', '--quiet', '-b', 'milligram-fixture', $pin))
    if ((& git -C $sampleRoot rev-parse HEAD) -ne $pin) { throw 'The reference project is not at its pinned revision.' }
    $sourceProject = Join-Path $sampleRoot 'src/Polly.Net45/Polly.Net45.csproj'
    $testProject = Join-Path $sampleRoot 'src/Polly.Net45.Specs/Polly.Net45.Specs.csproj'
    $result.sourceProjectSha256 = (Get-FileHash -LiteralPath $sourceProject).Hash
    $result.testProjectSha256 = (Get-FileHash -LiteralPath $testProject).Hash
    $result.msbuild = (@(& $MsBuildPath /nologo /version) -join ' ').Trim()
    $result.sdk = (& dotnet --version)
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) { $result.visualStudio = (@(& $vswhere -latest -products '*' -property installationVersion) -join ' ').Trim() }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $sampleRoot 'src/Polly.Net45.Specs/packages.config') -Raw
    foreach ($package in $manifest.packages.package) {
        Get-LegacyPackage $package.id $package.version (Join-Path $sampleRoot ('src/packages/' + $package.id + '.' + $package.version))
    }
    $references = Join-Path $sampleRoot '.milligram/net45'
    Get-LegacyPackage 'Microsoft.NETFramework.ReferenceAssemblies.net45' '1.0.3' $references '23A9F94EA3E2CB88CD8341AF75B811C6FB5CB82516FC696E95ED4620279128E3'
    $targets = [Security.SecurityElement]::Escape((Join-Path $references 'build/Microsoft.NETFramework.ReferenceAssemblies.net45.targets'))
    [IO.File]::WriteAllText((Join-Path $sampleRoot 'Directory.Build.targets'), '<Project><Import Project="' + $targets + '"/></Project>')
    $runnerRoot = Join-Path $sampleRoot '.milligram/xunit'
    Get-LegacyPackage 'xunit.runner.console' '2.9.3' $runnerRoot
    $runner = Join-Path $runnerRoot 'tools/net48/xunit.console.exe'
    $testAssembly = Join-Path $sampleRoot 'src/Polly.Net45.Specs/bin/Release/Polly.Net45.Specs.dll'
    $tools = Join-Path $sampleRoot '.milligram/tools'
    [void](Invoke-Legacy 'install-collector' 'dotnet' @('tool', 'install', 'dotnet-coverage', '--version', '18.11.2', '--tool-path', $tools))
    $collector = Join-Path $tools 'dotnet-coverage.exe'
    $result.collector = (@(& $collector --version) -join ' ').Trim()
    $result.runner = 'xunit.runner.console 2.9.3, net48; original xunit 2.1.0 tests target net45'
    $settings = Join-Path $sampleRoot '.milligram/coverage.config'
    [IO.File]::WriteAllText($settings, '<Configuration><CodeCoverage><ModulePaths><Include><ModulePath>.*[\\/]Polly\.dll$</ModulePath></Include></ModulePaths></CodeCoverage></Configuration>')
    Build-Legacy 'build-original'
    [void](Collect-Legacy 'full-pass')
    $filter = @('-method', 'Polly.Specs.ContextSpecs.Should_assign_ExecutionKey_from_constructor')
    $partial = Collect-Legacy 'partial-pass' $filter
    $testFile = Join-Path $sampleRoot 'src/Polly.SharedSpecs/ContextSpecs.cs'
    $original = [IO.File]::ReadAllBytes($testFile)
    try {
        $text = [Text.Encoding]::UTF8.GetString($original)
        $needle = 'context.ExecutionKey.Should().Be("SomeKey");'
        $offset = $text.IndexOf($needle, [StringComparison]::Ordinal)
        if ($offset -lt 0) { throw 'The pinned assertion changed.' }
        $text = $text.Substring(0, $offset) + 'context.ExecutionKey.Should().Be("Intentional coverage failure");' + $text.Substring($offset + $needle.Length)
        [IO.File]::WriteAllText($testFile, $text, [Text.UTF8Encoding]::new($false))
        Build-Legacy 'build-failing-test'
        $failing = Collect-Legacy 'partial-fail' $filter 1
    }
    finally { [IO.File]::WriteAllBytes($testFile, $original) }
    Build-Legacy 'build-restored'
    [IO.File]::WriteAllText((Join-Path $sampleRoot 'milligram.json'), '{ "prefix": "Polly", "scan": { "projects": ["src/Polly.Net45/Polly.Net45.csproj"], "configuration": "Release" }, "tests": { "projects": ["src/Polly.Net45.Specs/Polly.Net45.Specs.csproj"] }, "agent": { "enabled": false } }')
    Assert-ImportedCoverage $partial 'partial-pass'
    Assert-ImportedCoverage $failing 'partial-fail'
    Copy-Item -LiteralPath (Join-Path $sampleRoot '.milligram/model.json') -Destination (Join-Path $evidence 'model.json')

    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
    $start.WorkingDirectory = $sampleRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ToolAssembly, '--project', $sampleRoot, '--no-agent', '--no-browser', '--port', '15380')) { $start.ArgumentList.Add($argument) }
    $server = [Diagnostics.Process]::Start($start)
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        $serverFile = Join-Path $sampleRoot '.milligram/run/server.json'
        do {
            if ($server.HasExited) { throw "The legacy viewer exited $($server.ExitCode)." }
            if (Test-Path -LiteralPath $serverFile) {
                $address = (Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json).url.Replace('localhost', '127.0.0.1')
                $meta = Invoke-RestMethod ($address + 'api/meta')
                if ($meta.job.state -eq 'succeeded') { break }
            }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        if (!$address -or $meta.job.state -ne 'succeeded') { throw 'The legacy viewer did not finish scanning.' }
        $card = Invoke-RestMethod ($address + 'api/type?id=Polly.Context')
        foreach ($expected in @(@('ExecutionKey', 1.0), @('ExecutionGuid', 0.0))) {
            $member = @($card.members | Where-Object name -eq $expected[0])
            if ($member.Count -ne 1 -or $member[0].crap.coverage -ne $expected[1] -or $member[0].crapStale) { throw 'The live card disagrees with measured coverage.' }
            $source = Invoke-RestMethod ($address + 'api/source?file=' + [Uri]::EscapeDataString($member[0].file))
            $actual = [IO.File]::ReadAllText((Join-Path $sampleRoot $member[0].file))
            if ($source.text -cne $actual -or $member[0].line -lt 1 -or $member[0].endLine -gt ($actual.Split("`n").Length)) { throw 'The live member source link points at the wrong file or span.' }
        }
        $card | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $evidence 'context-card.json')
    }
    finally {
        if (!$server.HasExited) { $server.Kill($true); $server.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $evidence 'viewer.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $server.Dispose()
    }
    [void](Invoke-Legacy 'source-integrity' 'git' @('-C', $sampleRoot, 'diff', '--exit-code', '--', 'src'))
    $result.status = 'passed'
    Write-Host 'Legacy coverage passed: original full suite, passing/failing collection, current covered/uncovered cards and exact source links.'
}
catch { $result.status = 'failed'; $result.error = $_.Exception.Message; throw }
finally {
    $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = $previousTelemetry
    $result.packages = $packages
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
    Write-Host "Legacy evidence: $evidence; sample retained: $sampleRoot"
}
