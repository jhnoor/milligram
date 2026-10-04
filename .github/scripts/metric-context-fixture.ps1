param(
    [Parameter(Mandatory)][string]$ToolPath,
    [string[]]$PrefixArguments = @(),
    [switch]$Mutation
)

$ErrorActionPreference = 'Stop'
$metricRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram metrics ' + [guid]::NewGuid().ToString('N'))
function Write-Metric([string]$Name, [string]$Text) {
    $path = Join-Path $metricRoot $Name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}
function Invoke-Metric {
    & $ToolPath @PrefixArguments @args --project $metricRoot
    if ($LASTEXITCODE -ne 0) { throw "Milligram metric fixture failed ($LASTEXITCODE): $args. Fixture: $metricRoot" }
}
function Read-Metric([string]$Name) {
    return Get-Content -LiteralPath (Join-Path $metricRoot ".milligram/$Name.json") -Raw | ConvertFrom-Json -AsHashtable
}

Write-Metric 'milligram.json' '{ "scan": { "projects": ["Left/Left.csproj", "Right/Right.csproj"], "configuration": "Release" }, "tests": { "projects": ["Left.Tests/Tests.csproj", "Right.Tests/Tests.csproj"] } }'
Write-Metric 'Shared/Value.cs' @'
namespace Fixture {
    public class Shared {
        public bool Value() =>
#if LEFT
            true;
#else
            false;
#endif
    }
}
'@
foreach ($side in @('Left', 'Right')) {
    $framework = if ($side -eq 'Left') { '<TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks>' } else { '<TargetFramework>net10.0</TargetFramework>' }
    Write-Metric "$side/$side.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>$framework<DefineConstants>`$(DefineConstants);$($side.ToUpperInvariant())</DefineConstants></PropertyGroup>
  <ItemGroup><Compile Include="../Shared/Value.cs" Link="Value.cs"/></ItemGroup>
</Project>
"@
    Write-Metric "$side.Tests/Tests.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../$side/$side.csproj"/>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1"/>
    <PackageReference Include="xunit" Version="2.9.3"/>
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4"/>
    <PackageReference Include="coverlet.collector" Version="6.0.4"/>
  </ItemGroup>
</Project>
"@
    $assertion = if ($side -eq 'Left') { 'True' } else { 'False' }
    Write-Metric "$side.Tests/Check.cs" "public class Check { [Xunit.Fact] public void Value() => Xunit.Assert.$assertion(new Fixture.Shared().Value()); }"
    & dotnet restore (Join-Path $metricRoot "$side.Tests/Tests.csproj") --nologo
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $metricRoot" }
}

Invoke-Metric crap
$model = Read-Metric 'model'
$types = @($model.types | Where-Object { $_.name -eq 'Shared' })
if ($types.Count -ne 3) { throw "Expected three linked compiler contexts, got $($types.Count)" }
if (@($types | Where-Object { !$_.context.resolved -or $_.context.configuration -ne 'Release' }).Count -ne 0) { throw 'Compiler context metadata was not resolved' }
$measured = @($types | Where-Object { $_.context.framework -eq 'net10.0' })
$unmeasured = @($types | Where-Object { $_.context.framework -eq 'netstandard2.1' })
function Assert-MetricMembers($Snapshot, [string]$Kind) {
    foreach ($type in $measured) {
        $entry = $Snapshot.members[$type.members[0].id]
        if (!$entry -or $entry.hash -ne $type.members[0].hash) { throw "$Kind was not current for $($type.context.project)" }
        if ($Kind -eq 'coverage' -and $entry.coverage -ne 1) { throw 'Expected full coverage in each tested context' }
        if ($Kind -eq 'mutation' -and $entry.killed -lt 1) { throw 'Linked source mutants were not attributed to their owner' }
    }
    foreach ($type in $unmeasured) {
        if ($Snapshot.members.ContainsKey($type.members[0].id)) { throw "$Kind leaked into unmeasured framework $($type.context.framework)" }
    }
}
Assert-MetricMembers (Read-Metric 'metrics/crap') 'coverage'

if ($Mutation) {
    Write-Metric 'dotnet-tools.json' '{ "version": 1, "isRoot": true, "tools": { "dotnet-stryker": { "version": "5.0.0", "commands": ["dotnet-stryker"] } } }'
    Push-Location $metricRoot
    try {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw 'Stryker restore failed' }
        Invoke-Metric mutate 'Shared/Value.cs' --all
        Assert-MetricMembers (Read-Metric 'metrics/mutation') 'mutation'
    }
    finally { Pop-Location }
}

# An imported report has assembly names, but no framework provenance.
$report = @(Get-ChildItem -LiteralPath (Join-Path $metricRoot '.milligram/run/coverage') -Filter coverage.cobertura.xml -Recurse)[0].FullName
Invoke-Metric crap --coverage $report
$imported = Read-Metric 'metrics/crap'
foreach ($type in @($types | Where-Object { $_.context.project -eq 'Left/Left.csproj' })) {
    if ($imported.members.ContainsKey($type.members[0].id)) { throw 'Ambiguous imported assembly received fresh framework coverage' }
}
Write-Host "Metric context fixture passed: $metricRoot"
$resolved = [IO.Path]::GetFullPath($metricRoot)
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the temporary directory' }
Remove-Item -LiteralPath $resolved -Recurse -Force
