param([Parameter(Mandatory)][string]$ToolAssembly)
$ErrorActionPreference = 'Stop'
$scaleRoot = Join-Path ([IO.Path]::GetTempPath()) ('Milligram project scale ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scaleRoot) | Out-Null
$projects = 32
$types = 512
$lines = 0
for ($p = 0; $p -lt $projects; $p++) {
    $name = 'P' + $p.ToString('000')
    $directory = Join-Path $scaleRoot "src/$name"
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $reference = if ($p + 1 -lt $projects) { '<ProjectReference Include="../P' + ($p + 1).ToString('000') + '/P' + ($p + 1).ToString('000') + '.csproj"/>' } else { '' }
    [IO.File]::WriteAllText((Join-Path $directory "$name.csproj"), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Code.cs"/>' + $reference + '</ItemGroup></Project>')
    $code = [Text.StringBuilder]::new()
    [void]$code.AppendLine("namespace Bench.$name;")
    for ($t = 0; $t -lt $types; $t++) {
        [void]$code.AppendLine('public class C' + $t.ToString('000'))
        [void]$code.AppendLine('{')
        if ($t -eq 0 -and $p + 1 -lt $projects) { [void]$code.AppendLine('    public Bench.P' + ($p + 1).ToString('000') + '.C000 Next = new();') }
        [void]$code.AppendLine('    public int Compute(int value)')
        [void]$code.AppendLine('    {')
        for ($s = 0; $s -lt 55; $s++) { [void]$code.AppendLine("        if (value > $s) value--;") }
        [void]$code.AppendLine('        return value;')
        [void]$code.AppendLine('    }')
        [void]$code.AppendLine('}')
    }
    $text = $code.ToString()
    $lines += $text.Split("`n").Length - 1
    [IO.File]::WriteAllText((Join-Path $directory 'Code.cs'), $text)
    [IO.File]::WriteAllText((Join-Path $directory 'Unlisted.cs'), 'class Unlisted {}')
}
[IO.File]::WriteAllText((Join-Path $scaleRoot 'milligram.json'), '{"src":"src","prefix":"Bench","exclude":["**/bin/**","**/obj/**"]}')
& dotnet restore (Join-Path $scaleRoot 'src/P000/P000.csproj') --disable-parallel > (Join-Path $scaleRoot 'project-startup-scale-restore.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Synthetic graph restore failed' }
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($ToolAssembly, 'ir', '--project', $scaleRoot)) { $start.ArgumentList.Add($argument) }
$timer = [Diagnostics.Stopwatch]::StartNew()
$scan = [Diagnostics.Process]::Start($start)
$stdout = $scan.StandardOutput.ReadToEndAsync()
$stderr = $scan.StandardError.ReadToEndAsync()
$peak = 0L
try {
    while (!$scan.WaitForExit(250)) {
        $scan.Refresh()
        $peak = [Math]::Max($peak, $scan.PeakWorkingSet64)
        if ($timer.Elapsed.TotalMinutes -gt 12 -or $peak -gt 8GB) { $scan.Kill($true); throw 'Synthetic scan exceeded the experiment limit' }
    }
    $timer.Stop()
    [IO.File]::WriteAllText((Join-Path $scaleRoot 'project-startup-scale-scan.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    if ($scan.ExitCode -ne 0) { throw "Synthetic scan failed: $($scan.ExitCode)" }
    $file = [IO.File]::OpenRead((Join-Path $scaleRoot '.milligram/model.json'))
    try {
        $model = [Text.Json.JsonDocument]::Parse($file)
        try {
            $actualTypes = $model.RootElement.GetProperty('types').GetArrayLength()
            $actualEdges = $model.RootElement.GetProperty('edges').GetArrayLength()
            if ($actualTypes -ne $projects * $types -or $actualEdges -ne $projects - 1) { throw "Wrong graph: $actualTypes types, $actualEdges edges" }
        } finally { $model.Dispose() }
    } finally { $file.Dispose() }
    $report = @{ projects = $projects; physicalLines = $lines; types = $actualTypes; projectEdges = $actualEdges; seconds = $timer.Elapsed.TotalSeconds; mainProcessPeakBytes = $peak; excludesBuildHostMemory = $true }
    $report | ConvertTo-Json | Tee-Object -FilePath (Join-Path $scaleRoot 'project-startup-scale-result.json')
    Write-Output "Evidence: $scaleRoot"
} finally {
    if (!$scan.HasExited) { $scan.Kill($true); $scan.WaitForExit() }
    $scan.Dispose()
}
