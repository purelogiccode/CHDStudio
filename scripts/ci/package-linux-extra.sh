#!/usr/bin/env bash
# Builds the self-contained Linux extras for one release RID:
#   release_<version>_<rid>.AppImage
#   release_<version>_<rid>.flatpak
#
# Run on a native Linux runner for the target architecture. The primary Linux
# tar.gz remains framework-dependent; these two portable formats include the
# .NET runtime so they can launch without a separately installed runtime.
set -euo pipefail

usage() {
    echo "Usage: $0 --rid linux-x64|linux-arm64 --arch x86_64|aarch64 --version <version> --publish-dir <dir> --output-dir <dir>" >&2
    exit 2
}

RID=""
ARCH=""
VERSION=""
PUBLISH_DIR=""
OUTPUT_DIR=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid) RID="${2:-}"; shift 2 ;;
        --arch) ARCH="${2:-}"; shift 2 ;;
        --version) VERSION="${2:-}"; shift 2 ;;
        --publish-dir) PUBLISH_DIR="${2:-}"; shift 2 ;;
        --output-dir) OUTPUT_DIR="${2:-}"; shift 2 ;;
        *) usage ;;
    esac
done

[[ -n "$RID" && -n "$ARCH" && -n "$VERSION" && -n "$PUBLISH_DIR" && -n "$OUTPUT_DIR" ]] || usage
[[ "$RID" == "linux-x64" || "$RID" == "linux-arm64" ]] || usage
[[ "$ARCH" == "x86_64" || "$ARCH" == "aarch64" ]] || usage
[[ -d "$PUBLISH_DIR" ]] || { echo "Publish directory not found: $PUBLISH_DIR" >&2; exit 1; }
[[ -x "$PUBLISH_DIR/CHDStudio" ]] || { echo "Published CHDStudio executable not found." >&2; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_ID="com.purelogiccode.CHDStudio"
FLATPAK_BRANCH="25.08"
DATE="$(date -u +%F)"
STAGE="$(mktemp -d)"
APPDIR="$(mktemp -d)"
FLATPAK_BUILD="$(mktemp -d)"
FLATPAK_REPO="$(mktemp -d)"
TOOLS="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$APPDIR" "$FLATPAK_BUILD" "$FLATPAK_REPO" "$TOOLS"' EXIT

mkdir -p "$OUTPUT_DIR"
cp -a "$PUBLISH_DIR/." "$STAGE/"
rm -f "$STAGE"/*.xml "$STAGE"/*.pdb 2>/dev/null || true
rm -rf "$STAGE"/*/ 2>/dev/null || true
for extra in LICENSE.txt ReadMe.md WhatsNew.md; do
    cp "$ROOT/$extra" "$STAGE/$extra"
done
chmod 755 "$STAGE/CHDStudio"
[[ -f "$STAGE/7zz" ]] && chmod 755 "$STAGE/7zz"

# ---- AppImage ----------------------------------------------------------------
cp -a "$STAGE/." "$APPDIR/"
cp "$ROOT/packaging/linux/com.purelogiccode.CHDStudio.desktop" "$APPDIR/"
cp "$ROOT/CHDStudio/icon/chd.png" "$APPDIR/com.purelogiccode.CHDStudio.png"
cp "$ROOT/packaging/linux/AppRun" "$APPDIR/AppRun"
cp "$ROOT/CHDStudio/icon/chd.png" "$APPDIR/.DirIcon"
chmod 755 "$APPDIR/AppRun"

APPIMAGE_TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-${ARCH}.AppImage"
APPIMAGE_TOOL="$TOOLS/appimagetool"
echo "Downloading appimagetool 1.9.0 for ${ARCH}..."
curl -fsSL "$APPIMAGE_TOOL_URL" -o "$APPIMAGE_TOOL"
chmod 755 "$APPIMAGE_TOOL"

APPIMAGE_OUT="$OUTPUT_DIR/release_${VERSION}_${RID}.AppImage"
echo "Building $APPIMAGE_OUT..."
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$ARCH" VERSION="$VERSION" \
    "$APPIMAGE_TOOL" --no-appstream "$APPDIR" "$APPIMAGE_OUT"
chmod 755 "$APPIMAGE_OUT"

# ---- Flatpak -----------------------------------------------------------------
if ! command -v flatpak >/dev/null 2>&1; then
    echo "flatpak is required to build the .flatpak bundle." >&2
    exit 1
fi

echo "Building Flatpak app directory..."
flatpak build-init \
    --arch="$ARCH" \
    "$FLATPAK_BUILD" \
    "$APP_ID" \
    org.freedesktop.Sdk \
    org.freedesktop.Platform \
    "$FLATPAK_BRANCH"

mkdir -p "$FLATPAK_BUILD/files/bin" \
         "$FLATPAK_BUILD/files/share/applications" \
         "$FLATPAK_BUILD/files/share/icons/hicolor/1024x1024/apps" \
         "$FLATPAK_BUILD/files/share/metainfo"
cp -a "$STAGE/." "$FLATPAK_BUILD/files/bin/"
cp "$ROOT/packaging/linux/com.purelogiccode.CHDStudio.desktop" \
    "$FLATPAK_BUILD/files/share/applications/"
cp "$ROOT/CHDStudio/icon/chd.png" \
    "$FLATPAK_BUILD/files/share/icons/hicolor/1024x1024/apps/${APP_ID}.png"
sed -e "s/@VERSION@/$VERSION/g" -e "s/@DATE@/$DATE/g" \
    "$ROOT/packaging/linux/com.purelogiccode.CHDStudio.metainfo.xml" \
    > "$FLATPAK_BUILD/files/share/metainfo/${APP_ID}.metainfo.xml"
chmod 755 "$FLATPAK_BUILD/files/bin/CHDStudio"
[[ -f "$FLATPAK_BUILD/files/bin/7zz" ]] && chmod 755 "$FLATPAK_BUILD/files/bin/7zz"

flatpak build-finish \
    --command=CHDStudio \
    --share=ipc \
    --socket=x11 \
    --socket=wayland \
    --device=dri \
    --filesystem=host \
    --share=network \
    --talk-name=org.freedesktop.portal.Desktop \
    "$FLATPAK_BUILD"
flatpak build-export --arch="$ARCH" --subject="CHD Studio $VERSION" "$FLATPAK_REPO" "$FLATPAK_BUILD"

FLATPAK_OUT="$OUTPUT_DIR/release_${VERSION}_${RID}.flatpak"
echo "Building $FLATPAK_OUT..."
flatpak build-bundle \
    --arch="$ARCH" \
    --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
    "$FLATPAK_REPO" \
    "$FLATPAK_OUT" \
    "$APP_ID"

echo "Created:"
ls -l "$APPIMAGE_OUT" "$FLATPAK_OUT"
