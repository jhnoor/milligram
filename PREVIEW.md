# Install a tested preview

Preview packages let pilot users try a specific build with the .NET 10 SDK, without cloning
or building Milligram. Public NuGet publication is tracked in [#3](https://github.com/jhnoor/milligram/issues/3).

## Choose and download a build

1. Open [CI runs on main](https://github.com/jhnoor/milligram/actions/workflows/ci.yml?query=branch%3Amain).
2. Choose a completed, green run. All three operating-system jobs and formatting must pass.
3. Download the `milligram-preview` artifact and unzip it into a dedicated directory outside
   the repository you will examine. GitHub requires sign-in, even for public-repository artifacts.

With the GitHub CLI, the equivalent download is:

```sh
gh run download RUN_ID --repo jhnoor/milligram --name milligram-preview --dir milligram-preview
```

Replace `RUN_ID` with the selected run's number. Artifacts expire after 30 days; if one is
unavailable, select a newer green run. Older runs from before this feature have no preview artifact.
For a proposed fix, maintainers may provide a PR run instead: confirm its branch and complete
CI results before installing it. `SOURCE.txt` identifies the actual checked-out commit and run;
in PR runs this can be GitHub's temporary merge commit.

The artifact contains the exact package that passed the Ubuntu installed-tool and `dnx`
smoke checks, its version in `VERSION.txt`, provenance in `SOURCE.txt`, and `SHA256SUMS`.
The same source also passes package checks on Windows and macOS. This does not establish
authenticated Copilot or every physical terminal/browser combination; native hosting stays opt-in.

## Verify and run

On Linux, from the extracted directory, run `sha256sum -c SHA256SUMS`. On macOS, run
`shasum -a 256 -c SHA256SUMS`. Both must report `OK`.

On Windows PowerShell, from the extracted directory:

```powershell
$version = (Get-Content VERSION.txt -Raw).Trim()
$expected = ((Get-Content SHA256SUMS -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash "Milligram.$version.nupkg" -Algorithm SHA256).Hash -ne $expected) {
    throw 'Preview package checksum does not match.'
}
```

Copy the version from `VERSION.txt` into the command below, replacing `0.0.0-ci.RUN.ATTEMPT`.
Use absolute paths for the extracted preview and the C# repository:

```sh
dnx -y --source /absolute/path/to/milligram-preview "Milligram@0.0.0-ci.RUN.ATTEMPT" -- --project /absolute/path/to/repository
```

`dnx` downloads any required tool dependencies. It runs the pinned preview from its cache and
does not install or update a global `milligram` command. Verify the selected version by replacing
the arguments after `--` with `--version`. Add `--no-agent` to explore the diagram before setting
up Copilot. Run `doctor` with the same pinned package to check project prerequisites:

```sh
dnx -y --source /absolute/path/to/milligram-preview "Milligram@0.0.0-ci.RUN.ATTEMPT" -- doctor --project /absolute/path/to/repository
```

## Roll back and report feedback

Keep the earlier preview directory and version. Stop the viewer and its agent before changing
versions, then run the previous pinned command. If an agent was kept alive, stop it with the
version that started it (`agent stop --project ...`). Do not delete project policy or source files
to roll back. The preview does not alter your global Milligram installation.

Record the version, commit, OS, browser, repository size, command and observed result when
reporting a problem. Keep source and terminal transcripts private unless they are safe to share.
The pilot acceptance checklist is [#41](https://github.com/jhnoor/milligram/issues/41).
