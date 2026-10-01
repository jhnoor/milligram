param(
    [Parameter(Mandatory)][string]$ToolPath,
    [string[]]$PrefixArguments = @()
)

$ErrorActionPreference = 'Stop'
$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram Music Store ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sampleRoot) | Out-Null
$pin = 'e274968f2827c04cfefbe6493f0a784473f83f80'
function Invoke-SampleGit {
    & git -C $sampleRoot @args
    if ($LASTEXITCODE -ne 0) { throw "git $args failed ($LASTEXITCODE)" }
}
Invoke-SampleGit init --quiet
Invoke-SampleGit remote add origin https://github.com/SebastiaanLubbers/MvcMusicStore.git
Invoke-SampleGit fetch --quiet --depth 1 origin $pin
Invoke-SampleGit checkout --quiet --detach FETCH_HEAD
if ((& git -C $sampleRoot rev-parse HEAD) -ne $pin) { throw 'The reference application is not at its pinned commit' }
$projectFile = Join-Path $sampleRoot 'MvcMusicStore/MvcMusicStore.csproj'
$projectHash = (Get-FileHash -LiteralPath $projectFile -Algorithm SHA256).Hash
$evidence = Join-Path $sampleRoot '.milligram/evidence'
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$packageHashes = [ordered]@{}
function Get-SamplePackage([string]$Id, [string]$Version, [string]$Destination, [string]$ExpectedHash = '') {
    $normalized = $Version
    if ($normalized.Split('.').Count -eq 4 -and $normalized.EndsWith('.0', [StringComparison]::Ordinal)) { $normalized = $normalized.Substring(0, $normalized.Length - 2) }
    $lower = $Id.ToLowerInvariant()
    $packageFile = Join-Path $evidence ($lower + '.' + $normalized + '.nupkg')
    Invoke-WebRequest -Uri ('https://api.nuget.org/v3-flatcontainer/' + $lower + '/' + $normalized + '/' + $lower + '.' + $normalized + '.nupkg') -OutFile $packageFile
    $hash = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
    if ($ExpectedHash -and $hash -ne $ExpectedHash) { throw "Unexpected package content: $Id $Version" }
    $packageHashes[$Id + '/' + $Version] = $hash
    [IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $Destination)
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $sampleRoot 'MvcMusicStore/packages.config') -Raw
foreach ($package in $manifest.packages.package) {
    Get-SamplePackage $package.id $package.version (Join-Path $sampleRoot ('packages/' + $package.id + '.' + $package.version))
}
$referenceRoot = Join-Path $evidence 'reference-assemblies'
$webRoot = Join-Path $evidence 'web-targets'
Get-SamplePackage 'Microsoft.NETFramework.ReferenceAssemblies.net45' '1.0.3' $referenceRoot '23A9F94EA3E2CB88CD8341AF75B811C6FB5CB82516FC696E95ED4620279128E3'
Get-SamplePackage 'MSBuild.Microsoft.VisualStudio.Web.targets' '14.0.0.3' $webRoot '74B942705CB634BFC5EE8786FAA801CF9634CDD905511F273D3BBC8A60654419'
$webPath = [Security.SecurityElement]::Escape((Join-Path $webRoot 'tools/VSToolsPath'))
$referenceTargets = [Security.SecurityElement]::Escape((Join-Path $referenceRoot 'build/Microsoft.NETFramework.ReferenceAssemblies.net45.targets'))
[IO.File]::WriteAllText((Join-Path $sampleRoot 'Directory.Build.props'), '<Project><PropertyGroup><VSToolsPath>' + $webPath + '</VSToolsPath></PropertyGroup></Project>')
[IO.File]::WriteAllText((Join-Path $sampleRoot 'Directory.Build.targets'), '<Project><Import Project="' + $referenceTargets + '"/></Project>')
[IO.File]::WriteAllText((Join-Path $sampleRoot 'milligram.json'), '{ "prefix": "MvcMusicStore", "foreign": ["System.ComponentModel", "System.ComponentModel.DataAnnotations", "System.Transactions", "System.Web", "System.Data.Entity"] }')
[IO.File]::WriteAllText((Join-Path $sampleRoot 'MvcMusicStore/Models/UnlistedFixture.cs'), 'namespace MvcMusicStore { public class ExcludedFromCompile {} }')
$output = @(& $ToolPath @PrefixArguments ir --project $sampleRoot --msbuild MvcMusicStore/MvcMusicStore.csproj 2>&1)
$code = $LASTEXITCODE
$log = ($output | ForEach-Object { $_.ToString() }) -join "`n"
[IO.File]::WriteAllText((Join-Path $evidence 'scan.log'), $log)
Write-Output $log
if ($code -ne 0 -or $log -match 'Binding [^\r\n]*error CS|MSBuild Failure:') { throw 'The prepared reference application has incomplete declaration bindings' }
if (!$log.Contains('27 evaluated source file(s)')) { throw 'The pinned source membership changed' }
$model = Get-Content -LiteralPath (Join-Path $sampleRoot '.milligram/model.json') -Raw | ConvertFrom-Json
if ($model.types.Count -ne 32 -or $model.edges.Count -ne 98) { throw 'Unexpected pinned model size' }
if ($model.types.id -contains 'MvcMusicStore.ExcludedFromCompile') { throw 'An unlisted source entered the project model' }
foreach ($name in @('AppConfig', 'BundleConfig', 'WebApiConfig')) {
    if (!($model.edges | Where-Object { $_.from -eq ('MvcMusicStore.' + $name) -and $_.to -eq 'x:System.Web' })) { throw "Missing restored web dependency: $name" }
}
foreach ($pair in @(@('MvcMusicStore.MvcApplication', 'x:System.Web'), @('MvcMusicStore.Controllers.HomeController', 'x:System.Web'), @('MvcMusicStore.Models.MusicStoreEntities', 'x:System.Data.Entity'))) {
    if (!($model.edges | Where-Object { $_.from -eq $pair[0] -and $_.to -eq $pair[1] -and $_.kind -eq 'inheritance' })) { throw "Missing framework inheritance: $($pair[0])" }
}
if ((Get-FileHash -LiteralPath $projectFile -Algorithm SHA256).Hash -ne $projectHash) { throw 'The fixture modified the reference project file' }
$result = [ordered]@{
    commit = $pin; sdk = (& dotnet --version); projectSha256 = $projectHash
    types = $model.types.Count; edges = $model.edges.Count
    webEdges = @($model.edges | Where-Object to -eq 'x:System.Web').Count
    entityEdges = @($model.edges | Where-Object to -eq 'x:System.Data.Entity').Count
    packages = $packageHashes
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
Write-Output "Pinned legacy scan passed: 27 Compile files, 32 types, 98 dependencies, real net45 references and package bindings, no unlisted source. Evidence: $evidence"
