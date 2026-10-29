# AGENTS.md

Working instructions for AI/LLM sessions in this repository. Read this first; the
`docs/` folder is the authoritative project documentation (also published as the
GitHub wiki and GitHub Pages).

## What this is

`CHD Studio` - a cross-platform (Windows/Linux/macOS) Avalonia desktop
app that batch converts disc images (cue/iso/img/ccd/mds/pbp/cso/isz/ecm/split
sets/archives) to CHD using a bundled `chdman.exe` with a managed CHDSharp
fallback. The solution also contains six library projects and an xUnit test
project.

### The application project

- `CHDStudio` is the base app project: it multi-targets
  `net10.0` (Linux/macOS) and `net10.0-windows` (Windows), builds
  `CHDStudio.exe`, and links no files from outside its folder. The
  Windows TFM adds NAudio (Media Foundation/ACM MP3 decoding); the neutral TFM
  decodes MP3 via `ffmpeg` on PATH.
- UI-free code (`AppConfig.cs`, `Models`, `Utilities`, `Services`) lives in the
  app project alongside the UI layer (`MainWindow`, `AboutWindow`, `App`,
  `ScreenshotService`). There is no separate WPF front end any more.
- Encoder model: CHD creation uses the in-process CHDSharp library
  (`Services/ChdSharpEncoderService.cs`) on every platform. On Windows the
  bundled `chdman` is preferred and the built-in encoder is the automatic
  fallback; on Linux/macOS the built-in encoder is always used.
- Tool discovery is platform-aware: bundled tools next to the app come first
  (Windows: `chdman`/`7za`; Linux/macOS: `7zz`), then `chdman`, `7z`/`7za`/`7zz`
  on `PATH`. There is no bundled CHDSharp CLI any more.
- The custom title bar, status colors and terminal log are styled in
  `App.axaml`.

## Toolchain

- .NET SDK 10 - declared in `global.json` (`rollForward: latestMajor`). Never
  lower the Windows target below `net10.0-windows`; the Windows UI and NAudio
  need it.
- Node.js 24 - used only for the zero-dependency helper scripts in
  `scripts/ci/`. Do not add npm packages or a `package.json`.
- Avalonia 12.1 (desktop, Fluent theme, DataGrid); the neutral TFM must keep
  building without Windows-only packages so Linux/macOS publishes stay clean.

## Commands

```powershell
dotnet restore CSharp_CHDStudio.sln
dotnet build CSharp_CHDStudio.sln -c Release
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release

# CI runs the unit tests only: the [Trait("Category", "Integration")] classes
# read sample folders that exist on this machine (e.g. D:\Emulators\...) but
# not on GitHub runners. Run the full command above before a release.
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release --filter "Category!=Integration"

# Framework-dependent single-file publish (one per architecture)
dotnet publish CHDStudio/CHDStudio.csproj -c Release -f net10.0-windows -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64
dotnet publish CHDStudio/CHDStudio.csproj -c Release -f net10.0-windows -r win-arm64 --self-contained false -p:PublishSingleFile=true -o publish/win-arm64

# Release zip (same command CI runs)
./scripts/ci/package-release.ps1 -Rid win-x64 -Version 3.7.0 -PublishDir publish/win-x64 -OutputDir dist

# Run on Windows (multi-targeted, so pick the Windows TFM)
dotnet run --project CHDStudio/CHDStudio.csproj -f net10.0-windows

# Framework-dependent single-file publishes for Linux/macOS (neutral TFM)
dotnet publish CHDStudio/CHDStudio.csproj -c Release -f net10.0 -r linux-x64 --self-contained false -p:PublishSingleFile=true -o publish/linux-x64
dotnet publish CHDStudio/CHDStudio.csproj -c Release -f net10.0 -r osx-arm64 --self-contained false -p:PublishSingleFile=true -o publish/osx-arm64
```

## Release engineering (do not break)

- **The app is framework-dependent. It must NOT embed the .NET runtime.** Users
  install the .NET 10 Desktop Runtime on Windows and the .NET 10 runtime on
  Linux/macOS. Always publish with
  `--self-contained false -p:PublishSingleFile=true`. The output is a single
  `CHDStudio.exe` (Windows) or `CHDStudio` (Linux/macOS) plus the platform's
  native Avalonia libraries and bundled tools, which are content files and stay
  outside the bundle.
- **Release zips contain exactly one architecture's tools.** For `win-x64`:
  `7za.exe`, `chdman.exe`. For `win-arm64`: the `*_arm64.exe` variants. Linux
  and macOS get the official `7zz` console build plus `7-Zip-License.txt`.
  `scripts/ci/package-release.ps1` removes the other architecture's tools, the
  library `.xml` IntelliSense files and the native `.pdb` debug symbols (never
  used at runtime), and adds `LICENSE.txt`, `ReadMe.md` and `WhatsNew.md`. Do
  not put both architectures in one zip. CHD creation works without any bundled
  tool thanks to the built-in CHDSharp encoder.
- **Zip naming is fixed:** `release_<version>_<rid>.zip`, e.g.
  `release_3.9.0_win-x64.zip`. One zip per runtime identifier - `win-x64`,
  `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64` - and all
  of them are attached to the GitHub release.
- **Linux/macOS zips carry Unix execute bits.** Packaging runs on Windows for
  every RID, and `ZipFile` writes no Unix modes, so `package-release.ps1` stamps
  `0755` on `CHDStudio` and `7zz` (and `0644` on the remaining files); without
  that, `unzip` creates them non-executable.
- **Version lives in two csproj files** (`CHDStudio` and
  `CHDStudio.Tests`, `AssemblyVersion`/`FileVersion`) plus a matching
  section in `WhatsNew.md`. `scripts/ci/version.mjs` enforces all three. A
  release tag is `release_<version>` and must match the csproj version.
- **Cutting a release:** bump both csproj versions, add the `## <version>`
  section to the top of `WhatsNew.md`, commit to `master`, then
  `git tag release_<version> && git push origin release_<version>`. The
  `Release` workflow tests, publishes all six RIDs, zips them, and creates the
  GitHub release (title = version, body = the `WhatsNew.md` section).
  Re-running the workflow refreshes the notes and uploads assets with `--clobber`.

### Standard release routine (used for 3.11.0)

1. **Review the changes since the previous tag** and fix regressions before
   changing versions. Run the full suite, not only the CI filter.
2. **Bump the version** in `CHDStudio/CHDStudio.csproj` and
   `CHDStudio.Tests/CHDStudio.Tests.csproj` (`AssemblyVersion` and
   `FileVersion`), add the matching `## <version> (date)` section to the top of
   `WhatsNew.md`, copy that file byte-for-byte to `docs/WhatsNew.md`, and update
   `ReadMe.md` and the current-version package tables in `docs/`.
3. **Build and test**:
   ```powershell
   dotnet restore CSharp_CHDStudio.sln
   dotnet build CSharp_CHDStudio.sln -c Release
   dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release
   node scripts/ci/version.mjs
   jb inspectcode CSharp_CHDStudio.sln -o=InspectCode.log
   ```
   InspectCode must report zero warnings and zero errors before tagging.
4. **Publish all six framework-dependent single-file bundles**, never
   self-contained. Package to a versioned `dist/<version>` folder, never
   directly into the release archive:
   ```powershell
   $ErrorActionPreference = 'Stop'
   $version = "3.11.0"
   $rids = [ordered]@{
       'win-x64'     = 'net10.0-windows'
       'win-arm64'   = 'net10.0-windows'
       'linux-x64'   = 'net10.0'
       'linux-arm64' = 'net10.0'
       'osx-x64'     = 'net10.0'
       'osx-arm64'   = 'net10.0'
   }
   $outRoot = "dist/$version"
   New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
   foreach ($entry in $rids.GetEnumerator()) {
       $publishDir = "publish/$version/$($entry.Key)"
       dotnet publish CHDStudio/CHDStudio.csproj -c Release `
           -f $entry.Value -r $entry.Key --self-contained false `
           -p:PublishSingleFile=true -o $publishDir
       if ($LASTEXITCODE -ne 0) { throw "publish failed: $($entry.Key)" }
       ./scripts/ci/package-release.ps1 -Rid $entry.Key -Version $version `
           -PublishDir $publishDir -OutputDir $outRoot
       if ($LASTEXITCODE -ne 0) { throw "package failed: $($entry.Key)" }
   }
   ```
   If a publish fails with `MSB4018 ... deps.json ... being used by another
   process`, run `dotnet build-server shutdown`, wait a few seconds, and retry
   that RID. Do not delete the release archive to clear a lock.
5. **Verify every zip before archiving**:
   - required files: `LICENSE.txt`, `ReadMe.md`, `WhatsNew.md`;
   - the single-file `CHDStudio.exe`/`CHDStudio`, its native libraries, and only
     that RID's tools (`chdman.exe` + `7za.exe`, `chdman_arm64.exe` +
     `7za_arm64.exe`, or `7zz` + `7-Zip-License.txt`);
   - no other-architecture tools, no `*.pdb`, no `*.xml`, no .NET runtime
     (`hostfxr`, `hostpolicy`, `coreclr`, `System.Private.CoreLib`);
   - Linux/macOS entries `CHDStudio` and `7zz` have Unix mode `0100755`.
   Keep the names `release_<version>_<rid>.zip`.
6. **Archive locally without deleting anything**: copy the six new zips from
   `dist/<version>/` into `CHDStudio/bin/Release/`. That folder is an
   append-only local release archive; never remove older zips, posters, or any
   other file in it. Record the before/after file count.
7. **Commit and push `master`**, then tag and push the tag:
   ```powershell
   git add -A
   git commit -m "release: prepare 3.11.0"
   git tag release_3.11.0
   git push origin master
   git push origin release_3.11.0
   ```
   The tag must exactly match the csproj version and the `## 3.11.0` heading in
   `WhatsNew.md`. The `Protect master` ruleset lets repository admins bypass the
   PR requirement for this maintenance push; the release tag itself is not
   blocked.
8. **Let CI do the GitHub work**: the `Release` workflow validates the tag,
   runs the tests, publishes all six RIDs, verifies the Linux/macOS execute
   bits, creates a signed build-provenance attestation for each zip, creates or
   updates the GitHub release, and verifies that all six zip assets are
   attached. Do not create a second handwritten release for the same tag.
9. **Let the `Docs` workflow deploy documentation**: it builds Jekyll from
   `docs/`, verifies `docs/WhatsNew.md` is identical to the root file, deploys
   the GitHub Pages site and syncs the wiki (`Home.md`, `WhatsNew.md`,
   `_Sidebar.md` and the numbered pages). The wiki push needs the `WIKI_TOKEN`
   secret. Confirm Pages is using `build_type: workflow`.
10. **Verify after CI**:
    ```powershell
    gh release view release_3.11.0
    $attestDir = Join-Path $env:TEMP 'chdstudio-attest'
    New-Item -ItemType Directory -Force -Path $attestDir | Out-Null
    gh release download release_3.11.0 `
        --pattern release_3.11.0_win-x64.zip --dir $attestDir --clobber
    gh attestation verify (Join-Path $attestDir 'release_3.11.0_win-x64.zip') `
        --repo purelogiccode/CHDStudio
    gh run list --limit 10
    ```
    The release must be the latest, non-prerelease release with all six assets
    and the expected `WhatsNew.md` body; `CI`, `CodeQL`, `Docs`, `Dependency
    Review` (on PRs) and `Scorecard` must be green; Pages and the wiki must show
    3.11.0 and the side menu; the app's update check
    (`https://api.github.com/repos/purelogiccode/CHDStudio/releases/latest`)
    must see the new tag; and `CHDStudio/bin/Release/` must contain the six new
    zips beside every older file.

    The local `CHDStudio/bin/Release/` zips are built locally and are not
    byte-identical to CI's assets, so their hashes have no attestation. Run
    `gh attestation verify` against the downloaded GitHub release asset, not
    the local archive copy.

### Deployment invariants

- The app is framework-dependent and single-file: **never** use
  `--self-contained true` or embed the .NET runtime.
- Publish all six RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-x64`, `osx-arm64`.
- Every zip carries `LICENSE.txt`, `ReadMe.md` and `WhatsNew.md`.
- Version is in `CHDStudio/CHDStudio.csproj`, `CHDStudio.Tests/CHDStudio.Tests.csproj`,
  `WhatsNew.md` and `docs/WhatsNew.md`; `scripts/ci/version.mjs` enforces it.
- `docs/` is the only documentation source. Keep the Pages front matter and the
  wiki `_Sidebar.md` in sync; never edit the wiki UI.
- `CHDStudio/bin/Release/` is append-only. Copy in, never delete.
- Run `jb inspectcode` before tagging; the release gate is zero warnings and
  zero errors (notes are allowed).
- Run the full test suite before tagging; CI runs the unit filter only.


## Library NuGet packages (manual publish)

`PBPSharp` (<https://www.nuget.org/packages/PBPSharp>), `CSOSharp`
(<https://www.nuget.org/packages/CSOSharp>), `CCDSharp`
(<https://www.nuget.org/packages/CCDSharp>), `MDSSharp`
(<https://www.nuget.org/packages/MDSSharp>) and `ISZSharp`
(<https://www.nuget.org/packages/ISZSharp>) are published as NuGet packages.
Releases are **manual only**: do not add pack/push steps to the solution CI
workflows, and never commit or echo the API key. It is read from the
`NUGET_API_KEY` user environment variable.

- **Version** lives in the project file (`PBPSharp/PBPSharp.csproj`,
  `CSOSharp/CSOSharp.csproj`, `CCDSharp/CCDSharp.csproj`,
  `MDSSharp/MDSSharp.csproj` and `ISZSharp/ISZSharp.csproj`): bump
  `<Version>`, `<AssemblyVersion>` and `<FileVersion>` together. A pushed
  version is immutable - to change anything, bump and push again.
- **Target frameworks** are `net8.0;net9.0;net10.0` so both packages serve
  .NET 8, 9 and 10 consumers. `GenerateDocumentationFile` must stay on so each
  TFM ships its `.xml` next to the assembly.
- **Metadata** must keep `PackageProjectUrl` and `RepositoryUrl` pointed at
  <https://github.com/purelogiccode/CHDStudio>.
- **README**: `<Project>/README.md` is the package readme (packed at the package
  root as `README.md`). Keep it descriptive with usage examples and an API
  reference, and update it whenever the public surface changes.
- **Icon**: `<Project>/icon/icon.png` is packed as `icon.png` through its `None`
  item. It must stay under NuGet's 1 MB icon limit (currently ~280-300 KB at
  600x600). `icon.ico` stays in the repo only; do not pack a generated or
  resized copy.
- **XML docs**: every public member and, per this repo's convention, every
  method including private/internal ones carries documentation. The build must
  stay warning-free.

Release procedure (run from the repo root; substitute the project and package
name for the library being published):

```powershell
# 1. bump the three version fields in <project>.csproj, then:
dotnet build PBPSharp/PBPSharp.csproj -c Release        # or CSOSharp/CSOSharp.csproj, CCDSharp/CCDSharp.csproj

# library tests plus real-file integration tests (needs the local sample folder)
dotnet test CHDStudio.Tests/CHDStudio.Tests.csproj -c Release --filter "FullyQualifiedName~Pbp"   # or ~Cso
# (CCDSharp has no dedicated test class yet; run the full suite when it changes)

# 2. pack with package validation enabled
dotnet pack PBPSharp/PBPSharp.csproj -c Release -o <out-dir> -p:EnablePackageValidation=true

# 3. inspect the nupkg: README.md, icon.png, and lib/<tfm>/<name>.dll + .xml
#    for net8.0, net9.0 and net10.0

# 4. push with the user environment key (never hard-code it)
dotnet nuget push <out-dir>/PBPSharp.<version>.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json

# 5. verify indexing (takes a few minutes)
Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/pbpsharp/index.json   # or csosharp, ccdsharp
```

Before pushing, smoke-test the packed `.nupkg` in a throwaway consumer project
targeting `net8.0;net9.0;net10.0` (restore from a local package source and open
a real file on each runtime).

## CI workflows

| Workflow | Trigger | Purpose |
|---|---|---|
| `.github/workflows/ci.yml` | push/PR to `master`, manual | Restore, build, test; uploads trx + coverage results |
| `.github/workflows/release.yml` | tag `release_*`, manual with tag | Validate version, test, package zips for all six RIDs (Windows/Linux/macOS, x64+arm64), attest provenance, publish and verify the GitHub release |
| `.github/workflows/docs.yml` | push/PR to `master`, manual | PR: build the Jekyll Pages site; master: also deploy Pages and sync `docs/` to the GitHub wiki |
| `.github/workflows/codeql.yml` | push/PR to `master`, weekly, manual | CodeQL code scanning for C# and the Actions workflows (`security-and-quality`) |
| `.github/workflows/dependency-review.yml` | PR to `master` | Fail PRs that add high/critical vulnerable dependencies |
| `.github/workflows/scorecard.yml` | push to `master`, weekly, branch protection changes, manual | OpenSSF Scorecard; publishes results and uploads SARIF to code scanning |

All workflows set up Node 24 where scripts run. Every `uses:` reference is
pinned to a full commit SHA (with a `# vX` comment for Dependabot) because the
repository requires SHA pinning. Use the latest action majors
(`actions/checkout@v7`, `setup-dotnet@v6`, `setup-node@v7`, artifacts v7/v8) so
the runner's Node 24 runtime is used.

## Security and quality settings

- Dependabot alerts and security updates are enabled; `.github/dependabot.yml`
  adds weekly grouped NuGet and GitHub Actions version updates.
- Secret scanning and push protection are enabled. Private vulnerability
  reporting is enabled and `SECURITY.md` is the public reporting policy.
- Code scanning uses the advanced CodeQL workflow, not default setup (the two
  cannot run together). Do not enable CodeQL default setup without disabling
  `codeql.yml`.
- GitHub Code Quality default setup is enabled for C# and the Node helper
  scripts (`javascript-typescript`). It runs the dynamic
  `CodeQL - Code Quality` workflow.
- The `Protect master` ruleset requires a pull request and the CI/CodeQL/Docs/
  Dependency Review checks, blocks deletion and force-pushes, and lets
  repository admins bypass it for direct maintenance pushes.
- Actions are limited to SHA-pinned references, external fork PR workflows need
  approval, and the default `GITHUB_TOKEN` is read-only.
- Release zips get a signed build-provenance attestation. Verify a download with
  `gh attestation verify release_<version>_<rid>.zip --repo purelogiccode/CHDStudio`.

## Docs = GitHub Pages + GitHub wiki

- `docs/` is the single source of truth for both. Never edit the wiki in the
  GitHub UI.
- `scripts/ci/sync-wiki.mjs` copies `docs/*.md` to the wiki (`index.md` becomes
  `Home.md`, Jekyll front matter is stripped), adds `WhatsNew.md` (front matter
  stripped too), and syncs `docs/_Sidebar.md` as the wiki side menu.
- `WhatsNew.md` lives at the repository root and carries Jekyll front matter for
  the Pages site; `docs/WhatsNew.md` is a committed copy kept byte-identical by
  the Docs workflow (Jekyll ignores the underscore-prefixed `_Sidebar.md`).
- The wiki push needs a repository secret named `WIKI_TOKEN` (classic PAT with
  the `repo` scope, or fine-grained PAT with `Contents: Read and write`) because
  `GITHUB_TOKEN` cannot push to the wiki repository. Without the secret the sync
  step logs a message and skips.
- Pages deployment requires Settings -> Pages -> Source: "GitHub Actions".
- `docs/_config.yml` uses the `just-the-docs` remote theme and applies
  `layout: default` to every page through Jekyll `defaults`, which is what
  renders the Pages sidebar; keep the front matter (`title`, `nav_order`) on
  docs pages for navigation. The wiki side menu is `docs/_Sidebar.md`.

## Conventions

- Match existing code style; `DebugType` is `embedded` and analyzers
  (Meziantou, Roslynator) run on every build. Do not add code comments unless
  asked.
- Bundled binaries are committed in `CHDStudio/`: Windows
  `7za*.exe` and `chdman*.exe`, and `tools/7zz_*` (official 7-Zip console builds
  for Linux/macOS, copied next to the app as `7zz` for the matching RID). They
  are copied to the output with `CopyToOutputDirectory=Always`; only replace
  them deliberately and keep `tools/7-Zip-License.txt`.
- `CHDStudio/bin/Release/` is the local release archive: every
  version's `release_<version>_win-<rid>.zip` lives there, beside the per-TFM
  build output. Copy new zips in, **never delete files inside that path**
  (also avoid commands that would clean it).
- Tests are xUnit; add regression tests next to the existing ones in
  `CHDStudio.Tests/`. The suite must pass before a release.
  `[Trait("Category", "Integration")]` classes depend on local sample folders
  and are excluded from CI with `--filter "Category!=Integration"`.
