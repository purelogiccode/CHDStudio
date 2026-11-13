---
title: Release & Deployment
nav_order: 15
---

# 14. Release & Deployment

This page describes the maintainer release routine for **CHD Studio**. It is the
same process the `Release`, `CI` and `Docs` workflows use, and it keeps the
application bundles, GitHub release, GitHub Pages site and wiki in sync.

## 14.1 Versioning

The application version lives in two project files:

* `CHDStudio/CHDStudio.csproj` - `<AssemblyVersion>` and `<FileVersion>`
* `CHDStudio.Tests/CHDStudio.Tests.csproj` - `<AssemblyVersion>` and `<FileVersion>`

All four values must match, and `WhatsNew.md` must contain a section whose
heading starts with that exact version:

```markdown
## 3.14.0 (2026-11-13)
```

`scripts/ci/version.mjs` validates all of this locally and in CI. A release tag
is `release_<version>` and must match the project version exactly, for example
`release_3.14.0`.

## 14.2 Build the six runtime bundles

The app is **framework-dependent**: it must never embed the .NET runtime.
Publish with `--self-contained false -p:PublishSingleFile=true` so each output
contains one single-file `CHDStudio.exe`/`CHDStudio` application plus the native
Avalonia libraries and the matching bundled tools.

| RID | TFM | Bundle | Tools |
|---|---|---|---|
| `win-x64` | `net11.0-windows` | `.zip` | `chdman.exe`, `7za.exe` |
| `win-arm64` | `net11.0-windows` | `.zip` | `chdman_arm64.exe`, `7za_arm64.exe` |
| `linux-x64` | `net11.0` | `.tar.gz` | `7zz`, `7-Zip-License.txt` |
| `linux-arm64` | `net11.0` | `.tar.gz` | `7zz`, `7-Zip-License.txt` |
| `osx-x64` | `net11.0` | `.dmg` | `7zz`, `7-Zip-License.txt` |
| `osx-arm64` | `net11.0` | `.dmg` | `7zz`, `7-Zip-License.txt` |

Each bundle must be packaged on its native operating system: Windows zips on
Windows, Linux `.tar.gz` on Linux (so tar stores the real Unix modes), and
macOS `.dmg` with `hdiutil` on macOS. The `Release` workflow does this
automatically with one matrix job per RID. For a local build, publish and
package a RID with:

```powershell
$version = "3.14.0"
$rid = "win-x64"          # or linux-x64 / osx-arm64
$tfm = "net11.0-windows"  # net11.0 for Linux/macOS

dotnet publish CHDStudio/CHDStudio.csproj `
    -c Release `
    -f $tfm `
    -r $rid `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o "publish/$rid"

./scripts/ci/package-release.ps1 `
    -Rid $rid `
    -Version $version `
    -PublishDir "publish/$rid" `
    -OutputDir "dist"
```

`package-release.ps1` creates exactly one architecture's bundle, removes the
other architecture's tools and the unused `.xml`/`.pdb` files, and adds
`LICENSE.txt`, `ReadMe.md` and `WhatsNew.md`. Linux and macOS bundles carry a
single top-level `CHDStudio/` folder; the Windows zip is flat. The two
executables are set to `0755` and the data files to `0644` in the
mode-preserving formats. The resulting names are:

```text
release_<version>_win-x64.zip
release_<version>_win-arm64.zip
release_<version>_linux-x64.tar.gz
release_<version>_linux-arm64.tar.gz
release_<version>_osx-x64.dmg
release_<version>_osx-arm64.dmg
```

Copy the new bundles into `CHDStudio/bin/Release/` without deleting anything
that is already there. The release archive is append-only.

## 14.3 Verify before tagging

```powershell
dotnet build CSharp_CHDStudio.sln -c Release
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release
node scripts/ci/version.mjs
jb inspectcode --output=InspectCode.log CSharp_CHDStudio.sln
```

The full suite must pass (**1,304 tests**: 1,275 unit + 29 integration), and
InspectCode must report **zero warnings and zero errors**. The remaining
InspectCode notes are suggestions, not release blockers. For a Linux bundle,
extract the `.tar.gz` and confirm `CHDStudio` and `7zz` are executable; for a
macOS bundle, mount the `.dmg` and do the same. The release workflow repeats
these checks. Confirm that each bundle contains `LICENSE.txt`, `ReadMe.md`,
`WhatsNew.md`, the single-file application, the native libraries and only the
matching tools. The .NET runtime must not be inside the executable.

## 14.4 Documentation, Pages and wiki

`docs/` is the single source of truth for both documentation homes. Do not edit
the GitHub wiki directly.

* **Pages**: Jekyll with the `just-the-docs` theme. Every page has YAML front
  matter with `title` and `nav_order`; the theme renders the side menu. The
  color scheme is dark (`docs/_config.yml`).
* **Wiki**: `scripts/ci/sync-wiki.mjs` copies `docs/*.md` to the wiki,
  converting `index.md` to `Home.md`, stripping Jekyll front matter and syncing
  `docs/_Sidebar.md` to the wiki side menu.
* `WhatsNew.md` at the repository root is the release source. Keep
  `docs/WhatsNew.md` byte-identical to it; the Docs workflow fails the build
  when the two differ.

The `Docs` workflow builds the site on pull requests and, on `master`, deploys
Pages and pushes the wiki. The wiki push uses the `WIKI_TOKEN` repository
secret; without it the step reports that it skipped the sync.

## 14.5 Tag and let CI publish

After the docs and version files are committed and pushed to `master`:

```powershell
git tag release_3.14.0
git push origin release_3.14.0
```

The `Release` workflow then:

1. validates the tag against the project version and `WhatsNew.md`;
2. runs the test suite on Windows;
3. publishes all six RIDs with the framework-dependent single-file settings;
4. packages and verifies the six bundles (Windows `.zip`, Linux `.tar.gz`, macOS `.dmg`) on their native runners;
5. creates a signed build-provenance attestation for each bundle;
6. creates the GitHub release with the matching `WhatsNew.md` section as its body;
7. verifies that all six bundle assets are attached.

Re-running the workflow refreshes the release notes and uploads the assets with
`--clobber`. Verify a downloaded asset with:

```powershell
gh attestation verify release_<version>_<rid>.<ext> --repo purelogiccode/CHDStudio
```

The `Protect master` ruleset requires a pull request and the CI, CodeQL, Docs
and Dependency Review checks. Repository admins can bypass it for direct
maintenance pushes; remove the bypass if every change must go through a PR. See
[Security & Quality](15-security-and-quality.md) for the full CI/security setup.

## 14.6 Update-check compatibility

The application checks
`https://api.github.com/repos/purelogiccode/CHDStudio/releases/latest`, compares
the release tag with its assembly version, and opens the release page when a
newer version exists. Keep the tag format `release_<version>` and keep the
asset names `release_<version>_<rid>.<ext>` (`.zip`, `.tar.gz` or `.dmg`); both
are part of the release contract.

## 14.7 Post-release checks

* The GitHub release exists, is not marked prerelease, and has all six bundles.
* The release body is the new `WhatsNew.md` section.
* Each asset has a build-provenance attestation (`gh attestation verify`).
* The `CI`, `CodeQL`, `Docs` and `Scorecard` workflows are green on `master`.
* Pages shows the new version, dark side menu and the Security & Quality page.
* The wiki `Home`, `WhatsNew`, `_Sidebar` and Security & Quality pages reflect
  `docs/`.
* `CHDStudio/bin/Release/` contains the new six bundles and no older file was
  removed.
* The application's update check sees the new tag and offers it to older users.
