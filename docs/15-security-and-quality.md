---
title: Security & Quality
nav_order: 17
---

# 15. Security & Quality

This page documents how CHD Studio handles vulnerability reports and which
automated security and quality checks protect the repository and its release
artifacts. The public reporting policy is also available in
[`SECURITY.md`](https://github.com/purelogiccode/CHDStudio/blob/master/SECURITY.md).

## 15.1 Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security
problem.** Use GitHub's private vulnerability reporting:

<https://github.com/purelogiccode/CHDStudio/security/advisories/new>

Include the affected version, operating system and architecture, reproduction
steps, a minimal sample when possible, and the impact. The maintainers
acknowledge reports within 3 business days and coordinate disclosure with the
reporter. See [`SECURITY.md`](https://github.com/purelogiccode/CHDStudio/blob/master/SECURITY.md)
for the full scope, response timeline and safe-harbor terms.

## 15.2 Automated checks

| Workflow | Trigger | What it does |
|---|---|---|
| `CI` | push/PR to `master`, manual | Restores, builds, runs the 1145 unit tests with coverage, builds the neutral TFM on Linux and macOS, and verifies the version script |
| `CodeQL` | push/PR to `master`, weekly, manual | CodeQL `security-and-quality` analysis for C# and the GitHub Actions workflows |
| `Dependency Review` | pull request to `master` | Fails a PR that adds a high or critical vulnerable dependency |
| `Scorecard` | push to `master`, weekly, branch-protection changes, manual | OpenSSF Scorecard supply-chain checks; publishes results and uploads SARIF |
| `Docs` | push/PR to `master`, manual | Builds the Pages site, verifies `WhatsNew.md` is in sync, deploys Pages and syncs the wiki |
| `Release` | `release_*` tag, manual with tag | Validates the version, runs the full test suite, publishes all six RIDs, attests each zip, publishes the GitHub release and verifies all six assets |

GitHub Code Quality runs a separate dynamic `CodeQL - Code Quality` analysis for
C# and the Node helper scripts.

## 15.3 Repository settings

- **Dependabot alerts and security updates** are enabled. `.github/dependabot.yml`
  adds weekly grouped NuGet and GitHub Actions version updates.
- **Secret scanning and push protection** are enabled for the public repository.
  Non-provider patterns and validity checks require GitHub Secret Protection on
  the repository's current plan and are not enabled.
- **Private vulnerability reporting** is enabled.
- **Code scanning** uses the advanced CodeQL workflow, not CodeQL default setup.
  The two setups cannot run together; do not enable default setup while
  `.github/workflows/codeql.yml` is active.
- **Code Quality** default setup is enabled for C# and `javascript-typescript`.
- **Actions are SHA-pinned.** The repository requires full commit SHAs for every
  `uses:` reference; Dependabot updates the pinned SHA and its `# vX` comment.
- **External fork PR workflows require approval**, and the default
  `GITHUB_TOKEN` is read-only.
- **The `Protect master` ruleset** requires a pull request and the CI, CodeQL,
  Docs and Dependency Review checks, blocks deletion and force-pushes, and
  requires code scanning results. Repository admins can bypass it for direct
  maintenance pushes; remove the bypass if every change must go through a PR.

## 15.4 Release provenance

Every release zip is built by the `Release` workflow and receives a signed
build-provenance attestation. Verify a downloaded asset with the GitHub CLI:

```sh
gh attestation verify release_<version>_<rid>.zip --repo purelogiccode/CHDStudio
```

The local `CHDStudio/bin/Release/` archive is built separately and is not
byte-identical to CI's assets, so verify the downloaded GitHub release asset,
not the local archive copy.

The release workflow also checks that `release_<version>_win-x64.zip`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64` are all
attached before the job finishes.

## 15.5 Running the checks locally

```powershell
dotnet build CSharp_CHDStudio.sln -c Release
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release
node scripts/ci/version.mjs

# JetBrains InspectCode (the release gate requires zero warnings and errors)
jb inspectcode CSharp_CHDStudio.sln -o=InspectCode.log
```

The full suite currently passes **1175 tests** (1145 unit tests plus 30
integration tests); CI runs the unit tests only because the integration classes
need the maintainer's local sample folders. InspectCode reports **zero warnings
and zero errors**; the remaining notes are suggestions, not release blockers.

## 15.6 Release archive

Local release zips are archived in `CHDStudio/bin/Release/`. That folder is
append-only: copy the new six zips in and never delete older files. The zips
contain `LICENSE.txt`, `ReadMe.md`, `WhatsNew.md`, the framework-dependent
single-file application, the platform native libraries and only that RID's
tools. The .NET runtime is never embedded. See
[Release & Deployment](14-release-and-deployment.md) for the full routine.
