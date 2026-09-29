param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("Milligram smoke " + [guid]::NewGuid().ToString('N'))
$toolRoot = Join-Path $smokeRoot 'tool'
$projectRoot = Join-Path $smokeRoot 'project'
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed ($LASTEXITCODE)" }
}

Invoke-DotNet tool install Milligram --tool-path $toolRoot --source $packageSource --version $Version
$tool = Join-Path $toolRoot $(if ($IsWindows) { 'milligram.exe' } else { 'milligram' })
Push-Location $projectRoot
try {
    Invoke-DotNet new classlib -n Smoke -o src/Smoke --no-restore
    [IO.File]::WriteAllText((Join-Path $projectRoot 'src/Smoke/Class1.cs'), @'
namespace Smoke.Domain { public class Order { public int Total(int amount) => amount > 0 ? amount : 0; } }
namespace Smoke.Web { public class Handler { public Domain.Order Order { get; } = new(); } }
'@)
    $installedVersion = & $tool --version
    if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $Version) { throw "Installed version: $installedVersion; expected $Version" }
    $dnxVersion = @(& dnx -y --source $packageSource "Milligram@$Version" -- --version)
    if ($LASTEXITCODE -ne 0 -or $dnxVersion[-1] -ne $Version) {
        throw "dnx version: $dnxVersion; expected $Version"
    }

    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('dnx', '-y', '--source', $packageSource, "Milligram@$Version", '--', '--no-agent', '--no-browser', '--port', '15170')) {
        $start.ArgumentList.Add($argument)
    }
    $server = [Diagnostics.Process]::Start($start)
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        $view = $null
        $serverFile = Join-Path $projectRoot '.milligram/run/server.json'
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($server.HasExited) { throw "Viewer exited $($server.ExitCode)" }
            if (Test-Path -LiteralPath $serverFile) {
                try {
                    $address = (Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json).url
                    $view = Invoke-RestMethod ($address + 'api/view?context=real')
                    if ($view.nodes.Count -gt 0) { break }
                } catch { $view = $null }
            }
            Start-Sleep -Milliseconds 200
        }
        if (!$view -or $view.nodes.Count -eq 0) { throw 'Viewer did not return a diagram' }
        $meta = Invoke-RestMethod ($address + 'api/meta')
        if ($meta.types -ne 2) { throw "Expected two types, found $($meta.types)" }
        foreach ($asset in @('', 'app.js', 'style.css', 'lib/elk.bundled.js')) {
            $response = Invoke-WebRequest ($address + $asset)
            if ($response.StatusCode -ne 200 -or !$response.Content.Length) { throw "Missing viewer asset: $asset" }
        }
        $source = Invoke-RestMethod ($address + 'api/source?file=src/Smoke/Class1.cs')
        if (!$source.text.Contains('class Order')) { throw 'Source navigation did not return the source' }
        foreach ($file in @('milligram.json', '.milligram/agent.md', '.milligram/model.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $projectRoot $file))) { throw "First run did not create $file" }
        }
        Write-Output "Package $Version passed: installed tool, dnx, first-run policy, diagram, source and embedded assets."
    } finally {
        if (!$server.HasExited) { $server.Kill($true) }
        $server.WaitForExit()
        Write-Output $stdout.GetAwaiter().GetResult()
        Write-Output $stderr.GetAwaiter().GetResult()
        $server.Dispose()
    }
} finally {
    Pop-Location
}
