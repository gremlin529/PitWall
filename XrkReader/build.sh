#!/usr/bin/env bash
# Builds the XRK helper for macOS/Linux and stages it for the Avalonia app.
# Usage: ./build.sh [rust-target-triple]   (default: aarch64-apple-darwin)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
target="${1:-aarch64-apple-darwin}"
case "$target" in
  aarch64-apple-darwin) rid="osx-arm64" ;;
  x86_64-apple-darwin) rid="osx-x64" ;;
  x86_64-unknown-linux-gnu) rid="linux-x64" ;;
  aarch64-unknown-linux-gnu) rid="linux-arm64" ;;
  *) echo "Unsupported target $target" >&2; exit 1 ;;
esac

cargo build --release --locked --target "$target" --manifest-path "$here/Cargo.toml"

dest="$here/../PitWall.Avalonia/Assets/xrk/$rid"
mkdir -p "$dest"
cp "$here/target/$target/release/xrk-reader" "$dest/XrkReader"
chmod +x "$dest/XrkReader"
echo "Updated $dest/XrkReader"
