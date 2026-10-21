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
## 3.10.1 (2026-10-21)
```

`scripts/ci/version.mjs` validates all of this locally and in CI. A release tag
is `release_<version>` and must match the project version exactly, for example
`release_3.10.1`.

## 14.2 Build the six runtime bundles

The app is **framework-dependent**: it must never embed the .NET runtime.
Publish with `--self-contained false -p:PublishSingleFile=true` so each output
contains one single-file `CHDStudio.exe`/`CHDStudio` application plus the native
Avalonia libraries and the matching bundled tools.

| RID | TFM | Tools in the zip |
|---|---|---|
| `win-x64` | `net10.0-windows` | `chdman.exe`, `7za.exe` |
| `win-arm64` | `net10.0-windows` | `chdman_arm64.exe`, `7za_arm64.exe` |
| `linux-x64` | `net10.0` | `7zz`, `7-Zip-License.txt` |
| `linux-arm64` | `net10.0` | `7zz`, `7-Zip-License.txt` |
| `osx-x64` | `net10.0` | `7zz`, `7-Zip-License.txt` |
| `osx-arm64` | `net10.0` | `7zz`, `7-Zip-License.txt` |

From the repository root:

```powershell
$version = "3.10.1"
$rids = @(
    @{ Rid = 'win-x64';     Tfm = 'net10.0-windows' },
    @{ Rid = 'win-arm64';   Tfm = 'net10.0-windows' },
    @{ Rid = 'linux-x64';   Tfm = 'net10.0' },
    @{ Rid = 'linux-arm64'; Tfm = 'net10.0' },
    @{ Rid = 'osx-x64';     Tfm = 'net10.0' },
    @{ Rid = 'osx-arm64';   Tfm = 'net10.0' }
)

foreach ($item in $rids) {
    dotnet publish CHDStudio/CHDStudio.csproj `
        -c Release `
        -f $item.Tfm `
        -r $item.Rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -o "publish/$($item.Rid)"

    ./scripts/ci/package-release.ps1 `
        -Rid $item.Rid `
        -Version $version `
        -PublishDir "publish/$($item.Rid)" `
        -OutputDir "dist"
}
```

`package-release.ps1` creates exactly one architecture's bundle, removes the
other architecture's tools and the unused `.xml`/`.pdb` files, and adds
`LICENSE.txt`, `ReadMe.md` and `WhatsNew.md`. It stamps Unix execute bits on
`CHDStudio` and `7zz` in Linux/macOS zips. The resulting names are:

```text
release_<version>_win-x64.zip
release_<version>_win-arm64.zip
release_<version>_linux-x64.zip
release_<version>_linux-arm64.zip
release_<version>_osx-x64.zip
release_<version>_osx-arm64.zip
```

Copy the new zips into `CHDStudio/bin/Release/` without deleting anything that
is already there. The release archive is append-only.

## 14.3 Verify before tagging

```powershell
dotnet build CSharp_CHDStudio.sln -c Release
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release
node scripts/ci/version.mjs
```

For a non-Windows bundle, inspect `CHDStudio` and `7zz` inside the zip and
confirm their Unix mode is `0100755`; the release workflow repeats this check.
Confirm that each zip contains `LICENSE.txt`, `ReadMe.md`, `WhatsNew.md`, the
single-file application, the native libraries and only the matching tools.

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
git tag release_3.10.1
git push origin release_3.10.1
```

The `Release` workflow then:

1. validates the tag against the project version and `WhatsNew.md`;
2. runs the test suite on Windows;
3. publishes all six RIDs with the framework-dependent single-file settings;
4. packages and verifies the six `release_<version>_<rid>.zip` files;
5. creates the GitHub release with the matching `WhatsNew.md` section as its body.

Re-running the workflow refreshes the release notes and uploads the assets with
`--clobber`.

## 14.6 Update-check compatibility

The application checks
`https://api.github.com/repos/purelogiccode/CHDStudio/releases/latest`, compares
the release tag with its assembly version, and opens the release page when a
newer version exists. Keep the tag format `release_<version>` and keep the
asset names `release_<version>_<rid>.zip`; both are part of the release
contract.

## 14.7 Post-release checks

* The GitHub release exists, is not marked prerelease, and has all six zips.
* The release body is the new `WhatsNew.md` section.
* Pages shows the new version and dark side menu.
* The wiki `Home`, `WhatsNew` and `_Sidebar` pages reflect `docs/`.
* `CHDStudio/bin/Release/` contains the new six zips and no older file was
  removed.
* The application's update check sees the new tag and offers it to 3.9.x users.
