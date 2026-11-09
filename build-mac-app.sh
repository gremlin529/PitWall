#!/bin/bash
# Publishes the Avalonia app for macOS and wraps it in PitWall.app with the PitWall icon.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/PitWall.Avalonia/PitWall.Avalonia.csproj"
OUT="$ROOT/PitWall.Avalonia/publish/$RID"
APP="$OUT/PitWall.app"
STAGE="$ROOT/PitWall.Avalonia/publish/$RID-stage"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"

rm -rf "$OUT" "$STAGE"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained -o "$STAGE"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
mv "$STAGE"/* "$APP/Contents/MacOS/"
rmdir "$STAGE"
cp "$ROOT/Assets/Pitwall.icns" "$APP/Contents/Resources/Pitwall.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>PitWall</string>
  <key>CFBundleDisplayName</key><string>PitWall</string>
  <key>CFBundleIdentifier</key><string>com.pitwall.app</string>
  <key>CFBundleExecutable</key><string>PitWall</string>
  <key>CFBundleIconFile</key><string>Pitwall</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>${VERSION:-1.0}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION:-1.0}</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Ad-hoc signing lets the bundle launch on Apple Silicon.
codesign --force --deep --sign - "$APP"
echo "Built $APP"
