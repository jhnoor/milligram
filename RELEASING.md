# Releasing Milligram

Local builds are `0.0.0-dev`. A tag such as `v0.2.0` supplies the published version; a
tag such as `v0.2.0-beta.1` supplies a prerelease version. Do not hard-code a release
version in the project file.

## One-time setup

The repository owner must configure [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):

1. Sign in to the NuGet account that will own `Milligram` and create a trusted publishing policy.
2. Set repository owner `jhnoor`, repository `milligram`, workflow file `release.yml`, and
   environment `release`. Allow new packages and new versions, scoped to `Milligram`.
3. Create the GitHub environment `release`, with any required reviewers, and restrict it to release tags.
4. Set the GitHub secret `NUGET_USER` to the NuGet profile name, not an email address.

No long-lived NuGet API key is required. The workflow requests a short-lived credential
after validation. Check that the package id is available to the chosen account before releasing.

## Publish

After the changes have landed on `main` and CI is green:

```bash
git switch main
git pull --ff-only
git tag v0.2.0
git push origin v0.2.0
```

The release workflow runs build, tests, format and package smoke checks, packs the tagged
version, tests that exact package again, publishes it to NuGet, and attaches it to a GitHub
release. Package checks run on Linux, macOS and Windows and cover both local installation
and `dnx`, first-run configuration, HTTP endpoints and embedded viewer assets.

NuGet versions cannot be overwritten. Fix a bad release with a new version. Rerunning a
workflow after a successful push is safe: duplicate package uploads are skipped.

After the first stable release, verify `dnx Milligram` from a clean C# repository with only
the .NET 10 SDK, then promote it to the primary install command in the README. Do not claim
that the public command works before the package is published and verified.
