param(
    [Parameter(Mandatory)][string]$ToolPath,
    [string[]]$PrefixArguments = @()
)

$ErrorActionPreference = 'Stop'
$scanRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram project inputs ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scanRoot | Out-Null
$script:scanLog = ''

function Write-Input([string]$Name, [string]$Text) {
    $path = Join-Path $scanRoot $Name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}

function Scan-Project([string[]]$ScanArguments, [switch]$ExpectFailure) {
    $output = @(& $ToolPath @PrefixArguments ir --project $scanRoot @ScanArguments 2>&1)
    $code = $LASTEXITCODE
    $script:scanLog = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    Write-Output $script:scanLog | Out-Host
    if ($ExpectFailure) {
        if ($code -eq 0) { throw 'An invalid project unexpectedly succeeded' }
        if (!$script:scanLog.Contains('source-only')) { throw 'Evaluation failure did not explain the source-only fallback' }
        return
    }
    if ($code -ne 0) { throw "Project scan failed ($code)" }
    if (!$script:scanLog.Contains('evaluated source file(s)')) { throw 'The scan silently used the source-only fallback' }
    return Get-Content -LiteralPath (Join-Path $scanRoot '.milligram/model.json') -Raw | ConvertFrom-Json
}

function Require-Types($Model, [string[]]$Expected) {
    $actual = @($Model.types.id | Sort-Object)
    if (($actual -join '|') -ne (($Expected | Sort-Object) -join '|')) { throw "Wrong source membership: $($actual -join ', ')" }
}

function Require-Edge($Model, [string]$From, [string]$To, [bool]$Present = $true) {
    $found = @($Model.edges | Where-Object { $_.from -eq $From -and $_.to -eq $To }).Count -gt 0
    if ($found -ne $Present) { throw "Wrong dependency: $From -> $To (expected present: $Present)" }
}

Write-Input 'milligram.json' '{ "src": "unused", "exclude": ["**/obj/**", "App/Hidden.cs"], "foreign": ["System.Net.Http", "System.Text"] }'
Write-Input 'Settings.props' '<Project><PropertyGroup><DefineConstants>$(DefineConstants);IMPORTED</DefineConstants></PropertyGroup></Project>'
Write-Input 'App/App.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>enable</ImplicitUsings>
    <DefineConstants>$(DefineConstants);APP</DefineConstants>
  </PropertyGroup>
  <Import Project="../Settings.props"/>
  <ItemGroup>
    <Compile Include="App.cs"/>
    <Compile Include="Hidden.cs"/>
    <Compile Include="../Shared/Linked.cs" Link="Linked.cs"/>
    <Compile Include="Debug.cs" Condition="'$(Configuration)' == 'Debug'"/>
    <Compile Include="Release.cs" Condition="'$(Configuration)' == 'Release'"/>
    <ProjectReference Include="../Library/Library.csproj"/>
    <Using Include="System.Text" Alias="Text" Condition="'$(Configuration)' == 'Release'"/>
  </ItemGroup>
</Project>
'@
Write-Input 'App/App.cs' 'namespace App; public class Uses { public Lib.Target Target = new(); public HttpClient Client = new(); }'
Write-Input 'App/Hidden.cs' 'namespace App; public class Hidden {}'
Write-Input 'App/Unlisted.cs' 'namespace App; public class Unlisted {}'
Write-Input 'App/Debug.cs' @'
#if DEBUG && !RELEASE
namespace App; public class DebugOnly {}
#else
namespace App; public class WrongDebugSymbols {}
#endif
'@
Write-Input 'App/Release.cs' @'
#if DEBUG
namespace App; public class WrongReleaseSymbols {}
#else
namespace App; public class ReleaseOnly { public Text.StringBuilder Text = new(); }
#endif
'@
Write-Input 'Shared/Linked.cs' @'
#if APP && IMPORTED && NET10_0 && NET10_0_OR_GREATER
namespace Shared; public class Linked {}
#else
namespace Shared; public class ChangedSymbols {}
#endif
'@
Write-Input 'Library/Library.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>'
Write-Input 'Library/Library.cs' @'
namespace Lib;
public class Target {}
public class NoImport { public HttpClient Client = new(); }
#if APP || IMPORTED
public class LeakedSymbols {}
#else
public class OwnSymbols {}
#endif
'@

$common = @('App.Uses', 'Shared.Linked', 'Lib.Target', 'Lib.NoImport', 'Lib.OwnSymbols')
$debug = Scan-Project @('--msbuild', 'App/App.csproj', '--configuration', 'Debug')
Require-Types $debug ($common + 'App.DebugOnly')
Require-Edge $debug 'App.Uses' 'Lib.Target'
Require-Edge $debug 'App.Uses' 'x:System.Net.Http'
Require-Edge $debug 'Lib.NoImport' 'x:System.Net.Http' $false
if (($debug.types | Where-Object id -eq 'Shared.Linked').spans[0].file -ne 'Shared/Linked.cs') { throw 'Linked source lost its physical, root-relative path' }

$release = Scan-Project @('--msbuild', 'App/App.csproj', '--configuration', 'Release')
Require-Types $release ($common + 'App.ReleaseOnly')
Require-Edge $release 'App.ReleaseOnly' 'x:System.Text'
Require-Edge $release 'App.Uses' 'Lib.Target'

$generated = Join-Path $scanRoot 'App/obj/Debug/net10.0/App.GlobalUsings.g.cs'
if (!(Test-Path -LiteralPath $generated)) { throw 'The fixture did not create the stale generated-usings input' }
$appFile = Join-Path $scanRoot 'App/App.csproj'
[IO.File]::WriteAllText($appFile, [IO.File]::ReadAllText($appFile).Replace('<ImplicitUsings>enable</ImplicitUsings>', '<ImplicitUsings>disable</ImplicitUsings>'))
$disabled = Scan-Project @('--msbuild', 'App/App.csproj', '--configuration', 'Debug')
Require-Types $disabled ($common + 'App.DebugOnly')
Require-Edge $disabled 'App.Uses' 'x:System.Net.Http' $false
Require-Edge $disabled 'App.Uses' 'Lib.Target'
if (!(Test-Path -LiteralPath $generated)) { throw 'The stale input disappeared, so exclusion was not exercised' }

Write-Input 'Settings.props' '<Project/>'
$changed = Scan-Project @('--msbuild', 'App/App.csproj', '--configuration', 'Debug')
Require-Types $changed (($common | Where-Object { $_ -ne 'Shared.Linked' }) + @('Shared.ChangedSymbols', 'App.DebugOnly'))

Write-Input 'Legacy/Legacy.csproj' @'
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)/$(MSBuildToolsVersion)/Microsoft.Common.props"/>
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion><OutputType>Library</OutputType>
    <AssemblyName>Legacy</AssemblyName><Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
    <OutputPath>bin/$(Configuration)/</OutputPath><DefineConstants>LEGACY</DefineConstants>
  </PropertyGroup>
  <ItemGroup><Compile Include="Selected.cs"/></ItemGroup>
  <ItemGroup Condition="'$(Configuration)' == 'Release'"><Compile Include="Release.cs"/></ItemGroup>
  <Import Project="$(MSBuildToolsPath)/Microsoft.CSharp.targets"/>
</Project>
'@
Write-Input 'Legacy/Selected.cs' @'
#if LEGACY && !NET10_0
namespace Legacy { public class Selected {} }
#else
namespace Legacy { public class WrongFramework {} }
#endif
'@
Write-Input 'Legacy/Release.cs' 'namespace Legacy { public class ReleaseOnly {} }'
Write-Input 'Legacy/Unlisted.cs' 'namespace Legacy { public class Unlisted {} }'
$legacy = Scan-Project @('--msbuild', 'Legacy/Legacy.csproj', '--configuration', 'Release')
Require-Types $legacy @('Legacy.Selected', 'Legacy.ReleaseOnly')
$mixed = Scan-Project @('--msbuild', 'Legacy/Legacy.csproj', '--msbuild', 'App/App.csproj', '--configuration', 'Debug')
Require-Types $mixed (($common | Where-Object { $_ -ne 'Shared.Linked' }) + @('Shared.ChangedSymbols', 'App.DebugOnly', 'Legacy.Selected'))
Require-Edge $mixed 'App.Uses' 'Lib.Target'

$modelFile = Join-Path $scanRoot '.milligram/model.json'
$beforeFailure = [IO.File]::ReadAllText($modelFile)
Write-Input 'Broken.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project="missing.targets"/></Project>'
Scan-Project @('--msbuild', 'Broken.csproj') -ExpectFailure
if ([IO.File]::ReadAllText($modelFile) -ne $beforeFailure) { throw 'Failed evaluation overwrote the previous model' }

Write-Output "Project input fixture passed: SDK and legacy membership, linked files, configuration/framework symbols, imported properties, scoped imports, project-reference edges, stale generated inputs, mixed contexts, policy excludes and failure preservation. Evidence: $scanRoot"
