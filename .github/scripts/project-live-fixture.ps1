param(
    [Parameter(Mandatory)][string]$ToolPath,
    [string[]]$PrefixArguments = @()
)

$ErrorActionPreference = 'Stop'
$liveBase = Join-Path ([IO.Path]::GetTempPath()) ('Milligram live projects ' + [guid]::NewGuid().ToString('N'))
$liveRoot = Join-Path $liveBase 'repo'
$modelFile = Join-Path $liveRoot '.milligram/model.json'
function Write-Live([string]$Relative, [string]$Text) {
    $file = Join-Path $liveBase $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
    [IO.File]::WriteAllText($file, $Text)
}
function Read-LiveModel {
    if (Test-Path -LiteralPath $modelFile) { return Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json }
    return $null
}
function Wait-Live([string]$Description, [scriptblock]$Ready) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if ($server.HasExited) { throw "Viewer exited $($server.ExitCode): $Description" }
        if (& $Ready) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}
function Wait-Type([string]$Id) {
    Wait-Live "evaluated type $Id" { (Read-LiveModel).types.id -contains $Id }
}

Write-Live 'settings.extra' '<Project><PropertyGroup><DefineConstants>$(DefineConstants);IMPORTED</DefineConstants></PropertyGroup></Project>'
Write-Live 'input.schema' 'first generator input'
Write-Live 'repo/Directory.Build.props' '<Project><PropertyGroup><BaseIntermediateOutputPath>$(MSBuildProjectDirectory)/intermediate-cache/</BaseIntermediateOutputPath></PropertyGroup></Project>'
Write-Live 'repo/src/App/App.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <Import Project="../../../settings.extra"/>
  <Import Project="../../../optional.extra" Condition="Exists('../../../optional.extra')"/>
  <ItemGroup>
    <Compile Include="App.cs"/><Compile Include="../../Shared/*.cs"/>
    <AdditionalFiles Include="../../../input.schema"/>
  </ItemGroup>
</Project>
'@
Write-Live 'repo/src/App/App.cs' @'
namespace App;
public class Stable { public int Value() => 1; }
#if IMPORTED
public class Imported {}
#else
public class ChangedImport {}
#endif
#if DEBUG
public class DebugOnly {}
#else
public class ReleaseOnly {}
#endif
#if OPTIONAL
public class OptionalImport {}
#endif
'@
Write-Live 'repo/src/App/Unlisted.cs' 'namespace App; public class Unlisted {}'
Write-Live 'repo/Shared/Linked.cs' 'namespace App; public class Linked {}'

$start = [Diagnostics.ProcessStartInfo]::new($ToolPath)
$start.WorkingDirectory = $liveRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in ($PrefixArguments + @('--project', $liveRoot, '--no-agent', '--no-browser', '--port', '15370'))) { $start.ArgumentList.Add($argument) }
$server = [Diagnostics.Process]::Start($start)
$stdout = $server.StandardOutput.ReadToEndAsync()
$stderr = $server.StandardError.ReadToEndAsync()
try {
    Wait-Type 'App.Imported'
    Wait-Type 'App.DebugOnly'
    Wait-Type 'App.Linked'
    if ((Read-LiveModel).types.id -contains 'App.Unlisted') { throw 'Default startup included an unlisted source file' }
    $policyFile = Join-Path $liveRoot 'milligram.json'
    $policy = Get-Content -LiteralPath $policyFile -Raw | ConvertFrom-Json -AsHashtable
    if ($policy.scan.mode -ne 'auto') { throw 'Initialization did not persist automatic project selection' }
    $serverFile = Join-Path $liveRoot '.milligram/run/server.json'
    Wait-Live 'viewer address' { Test-Path -LiteralPath $serverFile }
    $address = (Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json).url.Replace('localhost', '127.0.0.1')
    Wait-Live 'startup scan finished' { (Invoke-RestMethod ($address + 'api/meta')).job.state -eq 'succeeded' }

    Write-Live 'optional.extra' '<Project><PropertyGroup><DefineConstants>$(DefineConstants);OPTIONAL</DefineConstants></PropertyGroup></Project>'
    Wait-Type 'App.OptionalImport'
    Remove-Item -LiteralPath (Join-Path $liveBase 'optional.extra')
    Wait-Live 'deleted optional import updates symbols' { (Read-LiveModel).types.id -notcontains 'App.OptionalImport' }

    Write-Live 'settings.extra' '<Project/>'
    Wait-Type 'App.ChangedImport'
    if ((Read-LiveModel).types.id -contains 'App.Imported') { throw 'An external import edit left stale symbols' }
    Write-Live 'repo/Shared/Linked.cs' 'namespace App; public class EditedLinked {}'
    Wait-Type 'App.EditedLinked'
    Write-Live 'repo/Shared/New.cs' 'namespace App; public class NewLinked {}'
    Wait-Type 'App.NewLinked'

    $beforeAdditional = (Read-LiveModel).generatedAt
    Write-Live 'input.schema' 'changed external generator input'
    Wait-Live 'external AdditionalFiles edit triggers evaluation' { (Read-LiveModel).generatedAt -ne $beforeAdditional }
    $debugHash = ((Read-LiveModel).types | Where-Object id -eq 'App.Stable').members[0].hash
    $policy.scan.configuration = 'Release'
    [IO.File]::WriteAllText($policyFile, ($policy | ConvertTo-Json -Depth 20))
    Wait-Type 'App.ReleaseOnly'
    if ((Read-LiveModel).types.id -contains 'App.DebugOnly') { throw 'Live configuration change kept Debug source' }
    $releaseHash = ((Read-LiveModel).types | Where-Object id -eq 'App.Stable').members[0].hash
    if ($releaseHash -eq $debugHash) { throw 'Live configuration change left unchanged source metrics looking current' }

    Wait-Live 'all queued scans finished' {
        $job = (Invoke-RestMethod ($address + 'api/meta')).job
        $job.state -eq 'succeeded' -and $job.queued.Count -eq 0
    }
    $stable = (Read-LiveModel).generatedAt
    Start-Sleep -Seconds 6
    if ((Read-LiveModel).generatedAt -ne $stable) { throw 'Design-time outputs caused an idle rescan loop' }

    $preserved = [IO.File]::ReadAllText($modelFile)
    Write-Live 'settings.extra' '<Project><Import Project="missing.targets"/></Project>'
    Wait-Live 'invalid import is reported' { (Invoke-RestMethod ($address + 'api/meta')).job.state -eq 'failed' }
    if ([IO.File]::ReadAllText($modelFile) -ne $preserved) { throw 'Invalid live evaluation replaced the previous model' }
    Write-Live 'settings.extra' '<Project><PropertyGroup><DefineConstants>$(DefineConstants);IMPORTED</DefineConstants></PropertyGroup></Project>'
    Wait-Type 'App.Imported'
    Write-Live 'repo/src/Other/Other.cs' 'namespace Other; public class Recovered {}'
    Write-Live 'repo/src/Other/Other.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project="../../../later.extra"/></Project>'
    $policy.scan.projects = @('src/Other/Other.csproj')
    [IO.File]::WriteAllText($policyFile, ($policy | ConvertTo-Json -Depth 20))
    Wait-Live 'new selection fails on its missing external import' { (Invoke-RestMethod ($address + 'api/meta')).job.state -eq 'failed' }
    if ((Read-LiveModel).types.id -notcontains 'App.Imported') { throw 'Failed project selection discarded the last good model' }
    Write-Live 'later.extra' '<Project/>'
    Wait-Type 'Other.Recovered'
    if ((Read-LiveModel).types.Count -ne 1) { throw 'The recovered selection retained types from the previous project' }
    Write-Output "Live project fixture passed: default startup, persisted selection, external/optional imports and AdditionalFiles, linked edits and new wildcard sources, live configuration, idle stability, failed evaluation/selection and recovery. Evidence: $liveBase"
} finally {
    if (!$server.HasExited) { $server.Kill($true) }
    $server.WaitForExit()
    Write-Output $stdout.GetAwaiter().GetResult()
    Write-Output $stderr.GetAwaiter().GetResult()
    $server.Dispose()
}
