param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { return }
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$pathRoot = Join-Path ([IO.Path]::GetTempPath()) ('mg å ' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$projectRoot = Join-Path $pathRoot 'project'
[IO.Directory]::CreateDirectory($projectRoot) | Out-Null
function Write-PathInput([string]$Name, [string]$Text) {
    $path = Join-Path $projectRoot $Name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}
function Invoke-PathDotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed ($LASTEXITCODE)" }
}
function Scan-PathProject([string]$Tool, [string]$Project, [switch]$ExpectPathFailure) {
    $info = [Diagnostics.ProcessStartInfo]::new($Tool)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @('ir', '--project', $projectRoot, '--msbuild', $Project)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (!$process.Start()) { throw 'Could not start the installed tool' }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Legacy path check did not finish within 30 seconds: $Project"
        }
        $log = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        Write-Output $log | Out-Host
        if ($ExpectPathFailure) {
            if ($process.ExitCode -eq 0 -or !$log.Contains('shorter --tool-path') -or !$log.Contains('Project requiring the legacy host:') -or !$log.Contains('Legacy.csproj')) {
                throw 'Deep legacy installation did not fail with the specific project and supported-path remedy'
            }
            if ($log.Contains('unable to connect')) { throw 'The legacy build host was launched before the path check' }
        } elseif ($process.ExitCode -ne 0) { throw "Installed scan failed: $Project ($($process.ExitCode))" }
        elseif ($log -match 'Binding [^\r\n]*error CS|MSBuild Failure:') { throw "Restored project has incomplete bindings: $Project" }
        Write-Output "Path fixture: $Project, exit $($process.ExitCode), $($clock.ElapsedMilliseconds) ms" | Out-Host
    } finally { $process.Dispose() }
}

$modern = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Modern.cs"/></ItemGroup></Project>'
Write-PathInput 'Modern/Modern.csproj' $modern
Write-PathInput 'Modern/Modern.cs' 'namespace Modern; public class Selected {}'
Write-PathInput 'Legacy/Legacy.csproj' @'
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)/$(MSBuildToolsVersion)/Microsoft.Common.props"/>
  <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><OutputType>Library</OutputType><AssemblyName>Legacy</AssemblyName><OutputPath>bin/Debug/</OutputPath></PropertyGroup>
  <ItemGroup><Compile Include="Legacy.cs"/></ItemGroup>
  <Import Project="$(MSBuildToolsPath)/Microsoft.CSharp.targets"/>
  <Import Project="Legacy.references.targets"/>
</Project>
'@
Write-PathInput 'Legacy/Legacy.cs' 'namespace Legacy { public class Selected {} }'
Write-PathInput 'Legacy/Unlisted.cs' 'class Unlisted {}'
Write-PathInput 'milligram.json' '{}'
Invoke-PathDotNet restore (Join-Path $projectRoot 'Modern/Modern.csproj') --nologo -v quiet
Write-PathInput 'references/References.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" PrivateAssets="all"/></ItemGroup></Project>'
Invoke-PathDotNet restore (Join-Path $projectRoot 'references/References.csproj') --nologo -v quiet
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'references/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$packagePath = $assets.libraries['Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3'].path
$referenceTargets = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ ($packagePath + '/build/Microsoft.NETFramework.ReferenceAssemblies.net48.targets') } | Where-Object { Test-Path -LiteralPath $_ })[0]
if (!$referenceTargets) { throw 'The restored .NET Framework reference targets were not found' }
Write-PathInput 'Legacy/Legacy.references.targets' ('<Project><Import Project="' + [Security.SecurityElement]::Escape($referenceTargets) + '"/></Project>')

foreach ($long in @($false, $true)) {
    $suffix = ".store/milligram/$Version/milligram/$Version/tools/net10.0/any/BuildHost-net472/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config"
    $padding = 264 - (Join-Path (Join-Path $pathRoot 'long ') $suffix).Length
    if ($padding -lt 1) { throw 'Temporary root is too deep for the 264-character fixture' }
    $directory = if ($long) { 'long ' + ('x' * $padding) } else { 'short' }
    $toolRoot = Join-Path $pathRoot $directory
    Invoke-PathDotNet tool install Milligram --tool-path $toolRoot --source $packageSource --version $Version
    $config = @(Get-ChildItem -LiteralPath $toolRoot -Filter 'Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config' -Recurse)
    if ($config.Count -ne 1 -or (($config[0].FullName.Length -ge 260) -ne $long)) { throw 'Fixture did not straddle the legacy configuration path limit' }
    if ($long -and $config[0].FullName.Length -ne 264) { throw 'Fixture missed the original failing configuration length' }
    Write-Output "Legacy configuration path: $($config[0].FullName.Length) characters"
    $tool = Join-Path $toolRoot 'milligram.exe'
    Scan-PathProject $tool 'Modern/Modern.csproj'
    $modelFile = Join-Path $projectRoot '.milligram/model.json'
    $model = Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json
    if ($model.types.Count -ne 1 -or $model.types[0].id -ne 'Modern.Selected') { throw 'Modern SDK membership changed' }
    $before = [IO.File]::ReadAllText($modelFile)
    Scan-PathProject $tool 'Legacy/Legacy.csproj' -ExpectPathFailure:$long
    if ($long) {
        if ([IO.File]::ReadAllText($modelFile) -ne $before) { throw 'The failed legacy scan replaced the previous model' }
        Write-PathInput 'Modern/Modern.csproj' ($modern.Replace('</Project>', '<ItemGroup><ProjectReference Include="../Legacy/Legacy.csproj"/></ItemGroup></Project>'))
        try { Scan-PathProject $tool 'Modern/Modern.csproj' -ExpectPathFailure }
        finally { Write-PathInput 'Modern/Modern.csproj' $modern }
        if ([IO.File]::ReadAllText($modelFile) -ne $before) { throw 'The failed referenced legacy scan replaced the previous model' }
        Scan-PathProject $tool 'Modern/Modern.csproj'
    } else {
        $model = Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json
        if ($model.types.Count -ne 1 -or $model.types[0].id -ne 'Legacy.Selected') { throw 'Short-path legacy membership changed' }
    }
}
Write-Output "Legacy host path fixture passed: short/long installed tools, spaces and Unicode, modern SDK scans, prompt legacy graph failures and model preservation. Evidence: $pathRoot"
