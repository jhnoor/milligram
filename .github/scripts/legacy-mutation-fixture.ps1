param(
    [Parameter(Mandatory)][string]$ToolAssembly,
    [Parameter(Mandatory)][string]$SampleRoot,
    [string]$EvidenceDirectory = 'artifacts/legacy-mutation'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$IsWindows) { throw 'The Framework mutation fixture requires Windows with full MSBuild and NuGet on PATH.' }
$ToolAssembly = [IO.Path]::GetFullPath($ToolAssembly)
$SampleRoot = [IO.Path]::GetFullPath($SampleRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$result = [ordered]@{ status = 'running'; sample = $SampleRoot; stryker = '5.0.0'; adapter = 'xunit.runner.visualstudio 2.4.5' }
$previousNoFetch = $env:GitVersion_NoFetchEnabled
$env:GitVersion_NoFetchEnabled = 'true'

function Invoke-MutationFixture([string]$Name, [string]$Command, [string[]]$Arguments, [int[]]$Expected = @(0)) {
    $lines = @(& $Command @Arguments 2>&1)
    $code = $LASTEXITCODE
    $text = ($lines | ForEach-Object { $_.ToString() }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $evidence "$Name.log"), $text)
    Write-Host ($lines | Select-Object -Last 25 | Out-String)
    if ($Expected.Count -gt 0 -and $Expected -notcontains $code) { throw "$Name exited $code; see $evidence/$Name.log." }
    return $code
}

function Read-MutationSnapshot {
    return Get-Content -LiteralPath (Join-Path $SampleRoot '.milligram/metrics/mutation.json') -Raw | ConvertFrom-Json -AsHashtable
}

function Read-Context {
    $model = Get-Content -LiteralPath (Join-Path $SampleRoot '.milligram/model.json') -Raw | ConvertFrom-Json -AsHashtable
    $types = @($model.types | Where-Object id -eq 'Polly.Context')
    if ($types.Count -ne 1 -or !$types[0].context.resolved) { throw 'The original Framework compiler context is unresolved.' }
    return $types[0]
}

function Assert-LiveMutation([string]$Name, $Snapshot) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
    $start.WorkingDirectory = $SampleRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ToolAssembly, '--project', $SampleRoot, '--no-agent', '--no-browser', '--port', '15381')) { $start.ArgumentList.Add($argument) }
    $server = [Diagnostics.Process]::Start($start)
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            if ($server.HasExited) { throw "The mutation viewer exited $($server.ExitCode)." }
            try { $meta = Invoke-RestMethod 'http://127.0.0.1:15381/api/meta' } catch { $meta = $null }
            if ($meta.job.state -eq 'succeeded') { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($meta.job.state -ne 'succeeded') { throw 'The mutation viewer did not finish scanning.' }
        $card = Invoke-RestMethod 'http://127.0.0.1:15381/api/type?id=Polly.Context'
        foreach ($member in $card.members | Where-Object { $Snapshot.members.ContainsKey($_.id) }) {
            $entry = $Snapshot.members[$member.id]
            if (!$member.mutation -or $member.mutationStale -or $member.mutation.hash -ne $entry.hash -or $member.mutation.killed -ne $entry.killed -or $member.mutation.survived -ne $entry.survived) { throw "Wrong live mutation score: $($member.name)" }
            $source = Invoke-RestMethod ('http://127.0.0.1:15381/api/source?file=' + [Uri]::EscapeDataString($member.file))
            $text = [IO.File]::ReadAllText((Join-Path $SampleRoot $member.file))
            if ($source.text -cne $text -or $member.line -lt 1 -or $member.endLine -gt $text.Split("`n").Length) { throw 'Wrong live mutation source link.' }
            foreach ($gap in $member.mutationGaps) {
                if ($gap.file -ne $member.file -or $gap.line -lt $member.line -or $gap.line -gt $member.endLine) { throw 'A surviving mutation points outside its source member.' }
            }
        }
        $card | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $evidence "$Name.card.json")
    }
    finally {
        if (!$server.HasExited) { $server.Kill($true); $server.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $evidence "$Name.viewer.log"), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $server.Dispose()
    }
}

function Assert-FailedMutation([string]$Name, [string]$Diagnostic) {
    $before = [IO.File]::ReadAllText($snapshotFile)
    [void](Invoke-MutationFixture $Name 'dotnet' @($ToolAssembly, 'mutate', $source, '--all', '--project', $SampleRoot) @(1))
    $log = Get-Content -LiteralPath (Join-Path $evidence "$Name.log") -Raw
    if (!$log.Contains($Diagnostic) -or !$log.Contains('without a report') -or !$log.Contains('milligram doctor')) { throw "Missing actionable failure details: $Name" }
    if ([IO.File]::ReadAllText($snapshotFile) -cne $before) { throw "$Name overwrote the prior mutation snapshot." }
    $result[$Name] = @{ exitCode = 1; previousSnapshotPreserved = $true }
}

try {
    $pin = '1a3bf7bf33cfeccce2e224f29cb273e7d333528c'
    if ((& git -C $SampleRoot rev-parse HEAD) -ne $pin) { throw 'Run the pinned legacy coverage fixture first.' }
    $result.commit = $pin
    $result.nuget = (@(& nuget help | Select-Object -First 1) -join '').Trim()
    [void](Invoke-MutationFixture 'source-before' 'git' @('-C', $SampleRoot, 'diff', '--exit-code', '--', 'src'))
    [IO.File]::WriteAllText((Join-Path $SampleRoot 'dotnet-tools.json'), '{ "version": 1, "isRoot": true, "tools": { "dotnet-stryker": { "version": "5.0.0", "commands": ["dotnet-stryker"], "rollForward": false } } }')
    Push-Location $SampleRoot
    try { [void](Invoke-MutationFixture 'install-stryker' 'dotnet' @('tool', 'restore')) }
    finally { Pop-Location }

    $adapterZip = Join-Path $SampleRoot '.milligram/xunit-vstest.nupkg'
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/xunit.runner.visualstudio/2.4.5/xunit.runner.visualstudio.2.4.5.nupkg' -OutFile $adapterZip
    $result.adapterSha256 = (Get-FileHash -LiteralPath $adapterZip).Hash
    if ($result.adapterSha256 -ne '1AFED4D553CA7CD6FB20E5ABC141942807AEACAB44DC0B5099E80314D9181C79') { throw 'Unexpected VSTest adapter package.' }
    $adapter = Join-Path $SampleRoot '.milligram/xunit-vstest'
    [IO.Compression.ZipFile]::ExtractToDirectory($adapterZip, $adapter)
    $targetsFile = Join-Path $SampleRoot 'Directory.Build.targets'
    [xml]$targets = [IO.File]::ReadAllText($targetsFile)
    $import = $targets.CreateElement('Import')
    $import.SetAttribute('Project', (Join-Path $adapter 'build/net462/xunit.runner.visualstudio.props'))
    $import.SetAttribute('Condition', "'`$(MSBuildProjectName)' == 'Polly.Net45.Specs'")
    [void]$targets.Project.AppendChild($import)
    $targets.Save($targetsFile)

    # The acceptance solution contains the unchanged net45 library and its original full test suite.
    $solution = Join-Path $SampleRoot 'src/Polly.Milligram.sln'
    [void](Invoke-MutationFixture 'create-solution' 'dotnet' @('new', 'sln', '--format', 'sln', '--name', 'Polly.Milligram', '--output', (Join-Path $SampleRoot 'src')))
    [void](Invoke-MutationFixture 'select-projects' 'dotnet' @('sln', $solution, 'add',
        (Join-Path $SampleRoot 'src/Polly.Net45/Polly.Net45.csproj'), (Join-Path $SampleRoot 'src/Polly.Net45.Specs/Polly.Net45.Specs.csproj')))
    $configuration = @{ 'stryker-config' = @{ solution = $solution; concurrency = 2; 'break-on-initial-test-failure' = $true; verbosity = 'debug' } }
    $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $SampleRoot 'src/Polly.Net45/stryker-config.json')
    Copy-Item -LiteralPath $solution -Destination (Join-Path $evidence 'Polly.Milligram.sln')
    $source = Join-Path $SampleRoot 'src/Polly.Shared/Context.cs'
    $snapshotFile = Join-Path $SampleRoot '.milligram/metrics/mutation.json'
    $fullExit = Invoke-MutationFixture 'full' 'dotnet' @($ToolAssembly, 'mutate', $source, '--all', '--project', $SampleRoot) @(0, 1)
    if ($fullExit -ne 0) {
        $probe = @('stryker', '--project', 'Polly.Net45.csproj', '--test-project', (Join-Path $SampleRoot 'src/Polly.Net45.Specs/Polly.Net45.Specs.csproj'),
            '--configuration', 'Release', '--mutate', '**/../Polly.Shared/Context.cs', '--reporter', 'json', '--reporter', 'progress', '--diag', '--log-to-file', '--skip-version-check')
        Push-Location (Join-Path $SampleRoot 'src/Polly.Net45')
        try {
            $result.automaticDiagnosticExitCode = Invoke-MutationFixture 'automatic-diagnostic' 'dotnet' ($probe + @('--output', (Join-Path $SampleRoot '.milligram/run/stryker/automatic-probe'))) @()
            $result.explicitMsbuild = (Get-Command msbuild).Source
            $result.overrideDiagnosticExitCode = Invoke-MutationFixture 'override-diagnostic' 'dotnet' ($probe + @('--msbuild-path', $result.explicitMsbuild, '--output', (Join-Path $SampleRoot '.milligram/run/stryker/override-probe'))) @()
        }
        finally { Pop-Location }
        throw 'Milligram mutation failed; inspect the automatic/explicit-MSBuild comparison before adding an override.'
    }
    $full = Read-MutationSnapshot
    $context = Read-Context
    $entries = @($context.members | ForEach-Object { $full.members[$_.id] } | Where-Object { $null -ne $_ })
    $killed = ($entries | Measure-Object -Property killed -Sum).Sum
    $survived = ($entries | Measure-Object -Property survived -Sum).Sum
    if ($killed -lt 1 -or $survived -lt 1) { throw 'Real original tests did not demonstrate both killed and surviving Context mutations.' }
    foreach ($member in $context.members) {
        if ($full.members.ContainsKey($member.id) -and $full.members[$member.id].hash -ne $member.hash) { throw 'Mutation was attributed to stale source.' }
    }
    $result.full = @{ killed = $killed; survived = $survived; members = $entries.Count }
    Copy-Item -LiteralPath $snapshotFile -Destination (Join-Path $evidence 'full.mutation.json')
    Assert-LiveMutation 'full' $full
    $unchanged = [IO.File]::ReadAllText($snapshotFile)
    [void](Invoke-MutationFixture 'unchanged' 'dotnet' @($ToolAssembly, 'mutate', $source, '--project', $SampleRoot))
    if ([IO.File]::ReadAllText($snapshotFile) -cne $unchanged -or !(Get-Content (Join-Path $evidence 'unchanged.log') -Raw).Contains('no changed members to mutate')) { throw 'Unchanged legacy source was unnecessarily mutated again.' }

    $sourceFile = $source
    $original = [IO.File]::ReadAllBytes($sourceFile)
    try {
        $text = [Text.Encoding]::UTF8.GetString($original)
        $needle = 'if (!_executionGuid.HasValue)'
        if (!$text.Contains($needle)) { throw 'The pinned Context implementation changed.' }
        [IO.File]::WriteAllText($sourceFile, $text.Replace($needle, 'if (_executionGuid.HasValue == false)'), [Text.UTF8Encoding]::new($false))
        [void](Invoke-MutationFixture 'differential' 'dotnet' @($ToolAssembly, 'mutate', $source, '--project', $SampleRoot))
        $changed = Read-MutationSnapshot
        $current = Read-Context
        $member = @($current.members | Where-Object name -eq 'ExecutionGuid')[0]
        if ($changed.members[$member.id].hash -eq $full.members[$member.id].hash -or $changed.members[$member.id].hash -ne $member.hash -or $changed.members[$member.id].killed -lt 1) { throw 'Differential mutation did not measure the changed member.' }
        foreach ($prior in $context.members | Where-Object { $_.id -ne $member.id -and $full.members.ContainsKey($_.id) }) {
            if (($changed.members[$prior.id] | ConvertTo-Json -Compress) -cne ($full.members[$prior.id] | ConvertTo-Json -Compress)) { throw 'Differential mutation changed an untouched member result.' }
        }
        Copy-Item -LiteralPath $snapshotFile -Destination (Join-Path $evidence 'differential.mutation.json')
        $result.differential = @{ member = $member.id; killed = $changed.members[$member.id].killed; survived = $changed.members[$member.id].survived }
        Assert-LiveMutation 'differential' $changed
    }
    finally { [IO.File]::WriteAllBytes($sourceFile, $original) }

    try {
        [IO.File]::WriteAllText($sourceFile, "#error MILLIGRAM_INTENTIONAL_BUILD_FAILURE`n" + [Text.Encoding]::UTF8.GetString($original).TrimStart([char]0xFEFF), [Text.UTF8Encoding]::new($false))
        Assert-FailedMutation 'failing-build' 'MILLIGRAM_INTENTIONAL_BUILD_FAILURE'
    }
    finally { [IO.File]::WriteAllBytes($sourceFile, $original) }

    $testFile = Join-Path $SampleRoot 'src/Polly.SharedSpecs/ContextSpecs.cs'
    $originalTest = [IO.File]::ReadAllBytes($testFile)
    try {
        $text = [Text.Encoding]::UTF8.GetString($originalTest)
        $needle = 'context.ExecutionKey.Should().Be("SomeKey");'
        $offset = $text.IndexOf($needle, [StringComparison]::Ordinal)
        if ($offset -lt 0) { throw 'The pinned test assertion changed.' }
        $text = $text.Substring(0, $offset) + 'context.ExecutionKey.Should().Be("MILLIGRAM_INTENTIONAL_TEST_FAILURE");' + $text.Substring($offset + $needle.Length)
        [IO.File]::WriteAllText($testFile, $text, [Text.UTF8Encoding]::new($false))
        Assert-FailedMutation 'failing-tests' 'MILLIGRAM_INTENTIONAL_TEST_FAILURE'
    }
    finally { [IO.File]::WriteAllBytes($testFile, $originalTest) }

    $manifestFile = Join-Path $SampleRoot 'dotnet-tools.json'
    $originalManifest = [IO.File]::ReadAllBytes($manifestFile)
    $originalPath = $env:PATH
    try {
        [IO.File]::WriteAllText($manifestFile, '{ "version": 1, "isRoot": true, "tools": {} }')
        $globalStryker = Get-Command dotnet-stryker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($globalStryker) {
            $directory = [IO.Path]::GetDirectoryName($globalStryker.Source)
            $env:PATH = ($env:PATH.Split([IO.Path]::PathSeparator) | Where-Object { $_.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -ine $directory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) }) -join [IO.Path]::PathSeparator
        }
        Assert-FailedMutation 'missing-stryker' 'dotnet-stryker'
    }
    finally {
        $env:PATH = $originalPath
        [IO.File]::WriteAllBytes($manifestFile, $originalManifest)
    }
    [void](Invoke-MutationFixture 'source-after' 'git' @('-C', $SampleRoot, 'diff', '--exit-code', '--', 'src'))
    $result.status = 'passed'
}
catch { $result.status = 'failed'; $result.error = $_.Exception.Message; throw }
finally {
    $env:GitVersion_NoFetchEnabled = $previousNoFetch
    $reports = Join-Path $SampleRoot '.milligram/run/stryker'
    if (Test-Path -LiteralPath $reports) { Copy-Item -LiteralPath $reports -Destination (Join-Path $evidence 'stryker') -Recurse }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
    Write-Host "Framework mutation evidence: $evidence"
}
