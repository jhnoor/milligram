param(
    [Parameter(Mandatory)][string]$ToolPath,
    [string[]]$PrefixArguments = @()
)

$ErrorActionPreference = 'Stop'
$frameworkRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram frameworks ' + [guid]::NewGuid().ToString('N'))
$script:frameworkLog = ''
function Write-Framework([string]$Name, [string]$Text) {
    $path = Join-Path $frameworkRoot $Name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}
function Invoke-FrameworkDotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed ($LASTEXITCODE)" }
}
function Scan-Framework([string[]]$Arguments, [switch]$MissingReference) {
    $output = @(& $ToolPath @PrefixArguments ir --project $frameworkRoot @Arguments 2>&1)
    $code = $LASTEXITCODE
    $script:frameworkLog = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    Write-Output $script:frameworkLog | Out-Host
    if ($code -ne 0) { throw "Framework scan failed ($code)" }
    if (!$script:frameworkLog.Contains('evaluated source file(s)')) { throw 'Framework scan silently fell back to source files' }
    if (!$MissingReference -and $script:frameworkLog -match 'Binding [^\r\n]*error CS|MSBuild Failure:') { throw 'A restored framework context has incomplete bindings' }
    return Get-Content -LiteralPath (Join-Path $frameworkRoot '.milligram/model.json') -Raw | ConvertFrom-Json
}
function Assert-FrameworkEdge($Model, [string]$From, [string]$To, [bool]$Present = $true) {
    $found = @($Model.edges | Where-Object { $_.from -eq $From -and $_.to -eq $To }).Count -gt 0
    if ($found -ne $Present) { throw "Wrong framework edge: $From -> $To (expected $Present)" }
}

Write-Framework 'milligram.json' '{ "src": "unused", "foreign": ["System.Web", "System.Threading", "Conditional.Left", "Conditional.Right"] }'
foreach ($side in @('Left', 'Right')) {
    Write-Framework "$side/$side.csproj" @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net48</TargetFramework><AssemblyName>Conditional</AssemblyName></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" PrivateAssets="all"/></ItemGroup>
</Project>
'@
    Write-Framework "$side/Target.cs" ('namespace Conditional.' + $side + ' { public class Target {} }')
    Invoke-FrameworkDotNet build (Join-Path $frameworkRoot "$side/$side.csproj") -c Release --nologo -v quiet
}
$assets = Get-Content -LiteralPath (Join-Path $frameworkRoot 'Left/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$packagePath = $assets.libraries['Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3'].path
$referenceTargets = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ ($packagePath + '/build/Microsoft.NETFramework.ReferenceAssemblies.net48.targets') } | Where-Object { Test-Path -LiteralPath $_ })[0]
if (!$referenceTargets) { throw 'The restored .NET Framework reference targets were not found' }
Write-Framework 'Legacy/Directory.Build.targets' ('<Project><Import Project="' + [Security.SecurityElement]::Escape($referenceTargets) + '"/></Project>')
Write-Framework 'Legacy/Legacy.csproj' @'
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)/$(MSBuildToolsVersion)/Microsoft.Common.props"/>
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion><OutputType>Library</OutputType><AssemblyName>Legacy</AssemblyName>
    <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration><OutputPath>bin/$(Configuration)/</OutputPath>
    <ChoicePath>../Right/bin/Release/net48</ChoicePath>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><ChoicePath>../Left/bin/Release/net48</ChoicePath><DefineConstants>DEBUG</DefineConstants></PropertyGroup>
  <ItemGroup><Compile Include="Source.cs"/><Reference Include="System.Web"/></ItemGroup>
  <ItemGroup Condition="'$(Configuration)' == 'Debug' Or '$(Configuration)' == 'Release'">
    <Reference Include="Conditional"><HintPath>$(ChoicePath)/Conditional.dll</HintPath></Reference>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)/Microsoft.CSharp.targets"/>
</Project>
'@
Write-Framework 'Legacy/Source.cs' @'
namespace Legacy {
    public class WebApp : System.Web.HttpApplication {}
    public class UsesPackage {
#if DEBUG
        public Conditional.Left.Target Value;
#else
        public Conditional.Right.Target Value;
#endif
    }
}
'@
foreach ($name in @('Multi', 'Library')) {
    $projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net48;net10.0</TargetFrameworks><LangVersion>latest</LangVersion></PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" PrivateAssets="all"/>
    <Reference Include="System.Web"/>
  </ItemGroup>
  REFERENCE
</Project>
'@
    $reference = if ($name -eq 'Multi') { '<ItemGroup><ProjectReference Include="../Library/Library.csproj"/></ItemGroup>' } else { '' }
    Write-Framework "$name/$name.csproj" ($projectTemplate.Replace('REFERENCE', $reference))
}
Write-Framework 'Library/Target.cs' @'
namespace Library {
    public class Target {
#if NET48
        public System.Web.HttpApplication Value;
#elif NET10_0
        public System.Threading.Lock Value;
#else
        public WrongFramework Value;
#endif
    }
}
'@
Write-Framework 'Multi/Source.cs' @'
namespace Multi { public class Uses { public Library.Target Value; } }
'@
Invoke-FrameworkDotNet restore (Join-Path $frameworkRoot 'Multi/Multi.csproj') --nologo -v quiet

$debug = Scan-Framework @('--msbuild', 'Legacy/Legacy.csproj', '--configuration', 'Debug')
Assert-FrameworkEdge $debug 'Legacy.WebApp' 'x:System.Web'
Assert-FrameworkEdge $debug 'Legacy.UsesPackage' 'x:Conditional.Left'
Assert-FrameworkEdge $debug 'Legacy.UsesPackage' 'x:Conditional.Right' $false
$release = Scan-Framework @('--msbuild', 'Legacy/Legacy.csproj', '--configuration', 'Release')
Assert-FrameworkEdge $release 'Legacy.UsesPackage' 'x:Conditional.Right'
Assert-FrameworkEdge $release 'Legacy.UsesPackage' 'x:Conditional.Left' $false

$mixed = Scan-Framework @('--msbuild', 'Legacy/Legacy.csproj', '--msbuild', 'Multi/Multi.csproj', '--configuration', 'Release')
if ($mixed.types.Count -ne 6) { throw 'Multi-target contexts were merged or omitted' }
foreach ($framework in @('net48', 'net10.0')) {
    $app = @($mixed.types | Where-Object { $_.name -eq 'Uses' -and $_.id.Contains($framework) })
    $library = @($mixed.types | Where-Object { $_.name -eq 'Target' -and $_.id.Contains($framework) })
    if ($app.Count -ne 1 -or $library.Count -ne 1) { throw "Missing unique $framework context" }
    Assert-FrameworkEdge $mixed $app[0].id $library[0].id
    $otherLibrary = ($mixed.types | Where-Object { $_.name -eq 'Target' -and !$_.id.Contains($framework) }).id
    Assert-FrameworkEdge $mixed $app[0].id $otherLibrary $false
    Assert-FrameworkEdge $mixed $library[0].id 'x:System.Web' ($framework -eq 'net48')
    Assert-FrameworkEdge $mixed $library[0].id 'x:System.Threading' ($framework -eq 'net10.0')
}
Assert-FrameworkEdge $mixed 'Legacy.WebApp' 'x:System.Web'

$missingAssembly = Join-Path $frameworkRoot 'Right/bin/Release/net48/Conditional.dll'
$savedAssembly = $missingAssembly + '.saved'
Move-Item -LiteralPath $missingAssembly -Destination $savedAssembly
try {
    $missing = Scan-Framework @('--msbuild', 'Legacy/Legacy.csproj', '--configuration', 'Release') -MissingReference
    if (!$script:frameworkLog.Contains('Conditional') -or !$script:frameworkLog.Contains('incomplete bindings')) { throw 'Missing package did not explain incomplete bindings' }
    Assert-FrameworkEdge $missing 'Legacy.UsesPackage' 'x:Conditional.Right' $false
    Assert-FrameworkEdge $missing 'Legacy.WebApp' 'x:System.Web'
} finally {
    Move-Item -LiteralPath $savedAssembly -Destination $missingAssembly
}
$repaired = Scan-Framework @('--msbuild', 'Legacy/Legacy.csproj', '--configuration', 'Release')
Assert-FrameworkEdge $repaired 'Legacy.UsesPackage' 'x:Conditional.Right'
Write-Output "Framework fixture passed: real net48/net10 reference assemblies, separate multi-target project-reference graphs, conditional/property-based HintPaths, missing-package diagnostics and repair. Evidence: $frameworkRoot"
