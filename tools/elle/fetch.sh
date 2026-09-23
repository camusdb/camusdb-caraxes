#!/usr/bin/env bash
# Downloads the elle-cli standalone jar that the Elle check of an append scenario runs
# (elle.jar defaults to tools/elle/elle-cli-0.1.11-standalone.jar). The release zip is pinned by
# SHA-256, so a changed upstream artifact fails here instead of checking histories with an
# unknown build.
set -euo pipefail

VERSION=0.1.11
ZIP_SHA256=9d3c72eaf3ccc4d0c7dc1da710264dee01f03af58c9fa177fec3f78ff5af1779
DIR="$(cd "$(dirname "$0")" && pwd)"
JAR="$DIR/elle-cli-$VERSION-standalone.jar"

if [[ -f "$JAR" ]]; then
  echo "already present: $JAR"
  exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

curl -fsSL -o "$TMP/elle.zip" \
  "https://github.com/ligurio/elle-cli/releases/download/$VERSION/elle-cli-bin-$VERSION.zip"
echo "$ZIP_SHA256  $TMP/elle.zip" | shasum -a 256 -c -
unzip -q "$TMP/elle.zip" -d "$TMP"
mv "$TMP/target/elle-cli-$VERSION-standalone.jar" "$JAR"
echo "installed: $JAR"
