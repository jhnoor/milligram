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

function Wait-Until([string]$Description, [scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Invoke-Action([hashtable]$Body) {
    $result = Invoke-RestMethod ($address + 'api/action') -Method Post -ContentType 'application/json' `
        -Headers @{ 'X-Milligram' = '1' } -Body ($Body | ConvertTo-Json -Depth 10)
    if (!$result.ok) { throw "Viewer action failed: $($result.message)" }
    return $result
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
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path (Join-Path $projectRoot 'cycle') -Target $projectRoot | Out-Null
    $outsideRoot = Join-Path $smokeRoot 'outside'
    New-Item -ItemType Directory -Path $outsideRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $outsideRoot 'Outside.cs'), 'generated outside-project fixture')
    New-Item -ItemType $linkType -Path (Join-Path $projectRoot 'outside-link') -Target $outsideRoot | Out-Null
    $outsideReturn = Join-Path $outsideRoot 'return'
    New-Item -ItemType $linkType -Path $outsideReturn -Target (Join-Path $projectRoot 'src') | Out-Null
    New-Item -ItemType $linkType -Path (Join-Path $projectRoot 'outside-return') -Target $outsideReturn | Out-Null
    $projectAlias = Join-Path $smokeRoot 'project-alias'
    New-Item -ItemType $linkType -Path $projectAlias -Target $projectRoot | Out-Null
    New-Item -ItemType $linkType -Path (Join-Path $projectRoot 'alias-link') -Target (Join-Path $projectAlias 'src') | Out-Null
    $obj = Join-Path $projectRoot 'src/Smoke/obj'
    New-Item -ItemType Directory -Path $obj -Force | Out-Null
    New-Item -ItemType $linkType -Path (Join-Path $obj 'cycle') -Target $obj | Out-Null
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
    $viewerRoot = if ($IsWindows) { $projectRoot.ToUpperInvariant() } else { $projectRoot }
    foreach ($argument in @('dnx', '-y', '--source', $packageSource, "Milligram@$Version", '--', '--project', $viewerRoot, '--no-agent', '--no-browser', '--port', '15170')) {
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
                    $address = (Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json).url.Replace('localhost', '127.0.0.1')
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
        $linkedSource = Invoke-RestMethod ($address + 'api/source?file=cycle/src/Smoke/Class1.cs')
        if ($linkedSource.text -ne $source.text -or $linkedSource.file -ne 'cycle/src/Smoke/Class1.cs') {
            throw 'An internal directory link lost its source or requested path'
        }
        $aliasedSource = Invoke-RestMethod ($address + 'api/source?file=alias-link/Smoke/Class1.cs')
        if ($aliasedSource.text -ne $source.text) { throw 'An ancestor alias hid an internal source file' }
        foreach ($outsideFile in @('outside-link/Outside.cs', 'outside-return/Smoke/Class1.cs')) {
            $outsideSource = Invoke-WebRequest ($address + 'api/source?file=' + $outsideFile) -SkipHttpErrorCheck
            $outsideOpen = Invoke-WebRequest ($address + 'api/open') -Method Post -ContentType 'application/json' `
                -Headers @{ 'X-Milligram' = '1' } -Body (@{ file = $outsideFile; line = 1 } | ConvertTo-Json) -SkipHttpErrorCheck
            if ($outsideSource.StatusCode -ne 404 -or $outsideOpen.StatusCode -ne 404) {
                throw 'Source preview or editor access followed a link outside the project'
            }
        }
        foreach ($file in @('milligram.json', '.milligram/agent.md', '.milligram/model.json')) {
            if (!(Test-Path -LiteralPath (Join-Path $projectRoot $file))) { throw "First run did not create $file" }
        }
        $policyFile = Join-Path $projectRoot 'milligram.json'
        $policyText = Get-Content -LiteralPath $policyFile -Raw
        $policy = $policyText | ConvertFrom-Json
        if ($policy.levels.Count -lt 2 -or $view.maxLevel -lt 1) { throw 'First-run diagram has no inferred layers' }

        $proposal = (Invoke-Action @{ op = 'new-proposal'; name = 'Package smoke' }).data.proposalId
        $proposalView = Invoke-RestMethod ($address + "api/view?context=$proposal")
        if (!$proposalView.context.isProposal -or $proposalView.context.name -ne 'Package smoke') { throw 'Proposal was not drawn' }
        Invoke-Action @{ op = 'rename-proposal'; context = $proposal; name = 'Renamed smoke' } | Out-Null
        $contexts = (Invoke-RestMethod ($address + 'api/meta')).contexts
        if (!($contexts | Where-Object { $_.id -eq $proposal -and $_.name -eq 'Renamed smoke' })) { throw 'Proposal rename was lost' }
        $mail = @(& $tool mail --peek | ForEach-Object { $_ | ConvertFrom-Json })
        if ($LASTEXITCODE -ne 0 -or !($mail | Where-Object { $_.op -eq 'context' -and $_.proposalId -eq $proposal })) { throw 'Proposal context did not reach the agent mailbox' }
        Invoke-Action @{ op = 'delete-proposal'; context = $proposal } | Out-Null
        if ((Invoke-RestMethod ($address + 'api/meta')).contexts.Count -ne 1) { throw 'Proposal deletion was lost' }
        foreach ($comment in ($policyText -split "`n" | Where-Object { $_.TrimStart().StartsWith('//') })) {
            if (!(Get-Content -LiteralPath $policyFile -Raw).Contains($comment.Trim())) { throw 'Viewer edits discarded policy comments' }
        }
        & $tool mail | Out-Null
        if ($LASTEXITCODE -ne 0 -or (& $tool mail --peek) -ne 'No mail.') { throw 'Mail was not consumed' }

        $coverageFile = Join-Path $projectRoot '.milligram/run/smoke-coverage.xml'
        [IO.File]::WriteAllText($coverageFile, '<coverage><packages><package><classes><class filename="src/Smoke/Class1.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages></coverage>')
        & $tool crap --coverage $coverageFile
        if ($LASTEXITCODE -ne 0) { throw 'Coverage import failed' }
        Wait-Until 'coverage appears on the live type card' {
            $card = Invoke-RestMethod ($address + 'api/type?id=Smoke.Domain.Order')
            ($card.members | Where-Object { $_.name -eq 'Total' }).crap.coverage -eq 1
        }

        $http = [Net.Http.HttpClient]::new()
        $stream = $http.GetStreamAsync($address + 'api/events').GetAwaiter().GetResult()
        $reader = [IO.StreamReader]::new($stream)
        $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
        try {
            & $tool tell notify 'Package smoke notification'
            if ($LASTEXITCODE -ne 0) { throw 'Agent reply command failed' }
            do {
                $line = $reader.ReadLineAsync($timeout.Token).AsTask().GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'Viewer event stream closed' }
            } until ($line.StartsWith('data: ') -and $line.Contains('Package smoke notification'))
        } finally {
            $timeout.Dispose()
            $reader.Dispose()
            $http.Dispose()
        }

        [IO.File]::AppendAllText((Join-Path $projectRoot 'src/Smoke/Class1.cs'), "`nnamespace Smoke.Domain { public class Added { } }")
        Wait-Until 'source edit updates the live diagram' { (Invoke-RestMethod ($address + 'api/meta')).types -eq 3 }
        $beforeMove = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'src/Smoke')).Path
        $afterMove = [IO.Path]::GetFullPath((Join-Path $projectRoot 'src/Renamed'))
        $projectPrefix = [IO.Path]::GetFullPath($projectRoot) + [IO.Path]::DirectorySeparatorChar
        if (!$beforeMove.StartsWith($projectPrefix, [StringComparison]::Ordinal) -or
            !$afterMove.StartsWith($projectPrefix, [StringComparison]::Ordinal)) { throw 'Folder move escaped the smoke project' }
        Move-Item -LiteralPath $beforeMove -Destination $afterMove
        Wait-Until 'folder move updates live source locations' {
            (Invoke-RestMethod ($address + 'api/type?id=Smoke.Domain.Order')).spans[0].file -eq 'src/Renamed/Class1.cs'
        }
        $modelFile = Join-Path $projectRoot '.milligram/model.json'
        $scanned = (Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json).generatedAt
        $projectFile = Join-Path $projectRoot 'src/Renamed/Smoke.csproj'
        $projectText = [IO.File]::ReadAllText($projectFile).Replace('<ImplicitUsings>enable</ImplicitUsings>', '<ImplicitUsings>disable</ImplicitUsings>')
        [IO.File]::WriteAllText($projectFile, $projectText)
        Wait-Until 'project edit triggers a new scan' { (Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json).generatedAt -ne $scanned }
        $scanned = (Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json).generatedAt
        Invoke-DotNet restore $projectFile
        Wait-Until 'restore triggers a new scan' { (Get-Content -LiteralPath $modelFile -Raw | ConvertFrom-Json).generatedAt -ne $scanned }
        Write-Output "Package $Version passed: installed tool, dnx, directory-link cycles and containment, first-run layers, proposals, mail round-trip, coverage import, live edits, folder moves, project edits, restore, source and embedded assets."
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
