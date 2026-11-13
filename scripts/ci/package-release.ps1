<#
.SYNOPSIS
    Builds one release bundle for a runtime identifier.

.DESCRIPTION
    Stages a published CHDStudio output folder, drops the binaries that
    belong to the other architecture, the library .xml IntelliSense files and the
    native .pdb debug symbols, adds LICENSE.txt, ReadMe.md and WhatsNew.md, and
    creates the platform's bundle:

      * release_<version>_win-<arch>.zip
      * release_<version>_linux-<arch>.tar.gz (with a CHDStudio/ top-level folder)
      * release_<version>_osx-<arch>.dmg (with a CHDStudio/ folder in the image)

    The app itself is published framework-dependent and single-file, so the .NET
    runtime is never bundled. Linux bundles must be built on Linux and macOS
    bundles on macOS: tar.gz preserves the Unix execute bits natively and the
    .dmg is created with hdiutil.

.PARAMETER Rid
    win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 or osx-arm64.

.PARAMETER Version
    Application version, e.g. 3.14.0.

.PARAMETER PublishDir
    Output folder of dotnet publish for the given Rid.

.PARAMETER OutputDir
    Folder that receives the release bundle.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishDir = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$outputDir = (Resolve-Path -LiteralPath $OutputDir).Path

$isWindowsRid = $Rid.StartsWith('win-')
$isMacRid = $Rid.StartsWith('osx-')
$isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows
)
$isLinuxHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Linux
)
$isMacHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::OSX
)

if ($isWindowsRid) {
    if (-not $isWindowsHost) {
        throw "Windows bundles must be packaged on Windows."
    }

    $extension = '.zip'
    $toolFiles = if ($Rid -eq 'win-x64') {
        @('7za.exe', 'chdman.exe')
    }
    else {
        @('7za_arm64.exe', 'chdman_arm64.exe')
    }
    $otherArchFiles = if ($Rid -eq 'win-x64') {
        @('7za_arm64.exe', 'chdman_arm64.exe')
    }
    else {
        @('7za.exe', 'chdman.exe')
    }
    $executable = 'CHDStudio.exe'
    $nativeFiles = @('av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')
}
elseif ($isMacRid) {
    if (-not $isMacHost) {
        throw "macOS bundles must be packaged on macOS (hdiutil is required for the .dmg)."
    }

    $extension = '.dmg'
    # Linux and macOS use the official 7-Zip console build copied as "7zz".
    $toolFiles = @('7zz', '7-Zip-License.txt')
    $otherArchFiles = @()
    $executable = 'CHDStudio'
    $nativeFiles = @(
        'libAvaloniaNative.dylib',
        'libHarfBuzzSharp.dylib',
        'libSkiaSharp.dylib'
    )
}
else {
    if (-not $isLinuxHost) {
        throw "Linux bundles must be packaged on Linux so tar.gz preserves the Unix execute bits."
    }

    $extension = '.tar.gz'
    $toolFiles = @('7zz', '7-Zip-License.txt')
    $otherArchFiles = @()
    $executable = 'CHDStudio'
    $nativeFiles = @('libHarfBuzzSharp.so', 'libSkiaSharp.so')
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("chdstudio-stage-" + [Guid]::NewGuid().ToString('N'))
$bundleRoot = Join-Path ([IO.Path]::GetTempPath()) ("chdstudio-bundle-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $stage -Recurse -Force

    # The release bundle is flat: any subdirectory in the publish output is either a
    # stale artifact of a reused output folder or an unintended payload, so drop it.
    Get-ChildItem -LiteralPath $stage -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force

    foreach ($name in $otherArchFiles) {
        $path = Join-Path $stage $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    # Library .xml IntelliSense files and native debug symbols (*.pdb) in the publish
    # output are never used at runtime; they do not belong in the release bundle.
    Get-ChildItem -LiteralPath $stage -Filter '*.xml' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    foreach ($extra in @('LICENSE.txt', 'ReadMe.md', 'WhatsNew.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $extra) -Destination $stage -Force
    }

    # The single-file executable still loads the platform's native rendering/text
    # libraries from beside it, so those must survive pruning.
    $expected =
        @($executable, 'LICENSE.txt', 'ReadMe.md', 'WhatsNew.md') + $nativeFiles + $toolFiles
    $missing = @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $stage $_)) })
    if ($missing.Count -gt 0) {
        throw "Release stage is missing required file(s): $($missing -join ', ')"
    }

    $bundlePath = Join-Path $outputDir "release_${Version}_${Rid}${extension}"
    if (Test-Path -LiteralPath $bundlePath) {
        Remove-Item -LiteralPath $bundlePath -Force
    }

    if ($isWindowsRid) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $stage,
            $bundlePath,
            [IO.Compression.CompressionLevel]::Optimal,
            $false
        )
    }
    else {
        # Linux/macOS bundles carry a single top-level "CHDStudio" folder, so extracting
        # the archive or mounting the image does not scatter files into the current folder.
        $bundleDir = Join-Path $bundleRoot 'CHDStudio'
        New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $bundleDir -Recurse -Force

        # The archive formats store real Unix modes rather than the zip ExternalAttributes
        # workaround. Normalize every entry first (0755 for the top-level folder, 0644 for
        # files), then set the two executables to 0755, so a checkout or publish output with
        # different source modes cannot leak 0777 entries into the bundle.
        & chmod 755 $bundleDir
        if ($LASTEXITCODE -ne 0) {
            throw "chmod failed for the bundle folder (exit code $LASTEXITCODE)."
        }
        Get-ChildItem -LiteralPath $bundleDir -File | ForEach-Object {
            & chmod 644 $_.FullName
            if ($LASTEXITCODE -ne 0) {
                throw "chmod failed for $($_.Name) (exit code $LASTEXITCODE)."
            }
        }
        foreach ($name in @($executable, '7zz')) {
            & chmod 755 (Join-Path $bundleDir $name)
            if ($LASTEXITCODE -ne 0) {
                throw "chmod failed for $name (exit code $LASTEXITCODE)."
            }
        }

        if ($isMacRid) {
            & hdiutil create `
                -volname "CHD Studio $Version" `
                -srcfolder $bundleRoot `
                -ov `
                -format UDZO `
                $bundlePath
            if ($LASTEXITCODE -ne 0) {
                throw "hdiutil failed to create $bundlePath (exit code $LASTEXITCODE)."
            }
        }
        else {
            & tar -czf $bundlePath -C $bundleRoot 'CHDStudio'
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed to create $bundlePath (exit code $LASTEXITCODE)."
            }
        }
    }

    Write-Host "Created $bundlePath"
    Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0,-24} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
    }
    Write-Host ("SHA256: " + (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash)
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $bundleRoot -Recurse -Force -ErrorAction SilentlyContinue
}
