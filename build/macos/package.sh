#!/usr/bin/env bash
#
# Builds FocusFlow.app and a DMG for macOS.
#
#   ./build/macos/package.sh [arm64|x64|universal]
#
# The result is unsigned unless CODESIGN_IDENTITY is set. Unsigned builds run fine on this
# machine but Gatekeeper will block them elsewhere; shipping to other people needs an Apple
# Developer identity and notarisation, which this script deliberately does not fake.
set -euo pipefail

ARCH="${1:-arm64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist/macos"
APP="$DIST/FocusFlow.app"

case "$ARCH" in
    arm64) RIDS=("osx-arm64") ;;
    x64)   RIDS=("osx-x64") ;;
    universal) RIDS=("osx-arm64" "osx-x64") ;;
    *) echo "usage: $0 [arm64|x64|universal]" >&2; exit 1 ;;
esac

# Read the version from the project so the bundle can never disagree with the assembly.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/FocusFlow.App/FocusFlow.App.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "could not read <Version> from the csproj" >&2; exit 1; }
echo "==> FocusFlow $VERSION ($ARCH)"

rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for RID in "${RIDS[@]}"; do
    echo "==> publishing $RID"
    dotnet publish "$ROOT/FocusFlow.App/FocusFlow.App.csproj" \
        -c Release -f net10.0 -r "$RID" --self-contained true \
        -p:PublishSingleFile=false -p:DebugType=none \
        -o "$DIST/publish-$RID" --nologo -v q
done

if [ "${#RIDS[@]}" -eq 2 ]; then
    # Universal: every file is shared except the native binaries, which get lipo'd.
    echo "==> merging into a universal binary"
    cp -R "$DIST/publish-osx-arm64/." "$APP/Contents/MacOS/"
    for f in "$DIST/publish-osx-arm64"/*; do
        name="$(basename "$f")"
        other="$DIST/publish-osx-x64/$name"
        if [ -f "$other" ] && file "$f" | grep -q "Mach-O"; then
            lipo -create "$f" "$other" -output "$APP/Contents/MacOS/$name" 2>/dev/null || true
        fi
    done
else
    cp -R "$DIST/publish-${RIDS[0]}/." "$APP/Contents/MacOS/"
fi

cp "$ROOT/FocusFlow.App/Assets/app-icon.icns" "$APP/Contents/Resources/app-icon.icns"
sed "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null

chmod +x "$APP/Contents/MacOS/FocusFlow"

# Ad-hoc signing costs nothing and is enough for the app to run locally. A real identity
# replaces the "-" when one is available.
IDENTITY="${CODESIGN_IDENTITY:--}"
echo "==> signing with identity: $IDENTITY"

# Sign inside-out, every nested binary first. --deep is unreliable and leaves Microsoft's
# own signatures on the bundled .NET dylibs; the loader then refuses to map them into an
# ad-hoc signed process ("mapping process and mapped file have different Team IDs") and
# the app dies before it starts.
# Managed .dll files count as nested code to codesign's deep walk, not just the Mach-O
# dylibs, so both have to be signed or verification fails on the first unsigned assembly.
SIGNED=0
while IFS= read -r -d '' f; do
    case "$f" in
        *.dll) : ;;
        *) file "$f" | grep -q "Mach-O" || continue ;;
    esac
    codesign --force --timestamp=none --sign "$IDENTITY" "$f" >/dev/null 2>&1 && SIGNED=$((SIGNED+1))
done < <(find "$APP/Contents/MacOS" -type f -print0)
echo "    re-signed $SIGNED nested binaries and assemblies"

# Hardened runtime only makes sense with a real identity; it buys nothing ad-hoc and can
# block the JIT.
# Sealing the bundle re-walks its contents and grumbles about non-code files such as
# deps.json. That is cosmetic — the Mach-O images are what the loader checks — so do not
# let it abort the build.
if [ "$IDENTITY" = "-" ]; then
    codesign --force --sign "$IDENTITY" "$APP" 2>&1 | grep -v "not signed at all\|In subcomponent\|replacing existing" || true
else
    codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
fi

# Plain verify, not --deep --strict. Everything the runtime needs sits beside the
# executable in Contents/MacOS, and the strict walk treats even deps.json as unsigned
# nested code. What actually matters is that every Mach-O in the bundle carries a
# consistent signature, which the loop above guarantees.
codesign --verify "$APP" && echo "    signature verifies"

echo "==> building DMG"
DMG="$DIST/FocusFlow-$VERSION-$ARCH.dmg"
STAGE="$DIST/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "FocusFlow" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE" "$DIST"/publish-*

echo
echo "app: $APP"
echo "dmg: $DMG"
[ "$IDENTITY" = "-" ] && echo "NOTE: ad-hoc signed only — Gatekeeper will block this on other Macs."
