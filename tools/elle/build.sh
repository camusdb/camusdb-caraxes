#!/usr/bin/env bash
# Builds the elle-cli jar that the Elle check runs (elle.jar defaults to the path this script writes).
#
# Why a build and not a release download: the last elle-cli release, 0.1.11 of 2026-09-01, bundles
# Elle 0.2.7 and Bifurcan 0.2.0-alpha7. Jepsen 0.3.15 (2026-10-16) fixed a safety defect in Elle's
# rw-register checker that came from two Bifurcan defects (lacuna/bifurcan issues 59 and 60: a set
# union mutated an immutable set, and equal sets compared unequal). The fix is Elle 0.2.8, which pulls
# the repaired Bifurcan (com.aphyr/bifurcan 0.2.0-rc4) through jepsen.history 0.1.9. elle-cli's main
# branch moved to Elle 0.2.8 on 2026-10-16 (commit 3c25048) without a release, so this builds that
# commit with Leiningen inside a Docker container; the host needs Docker and git only.
#
# The result is checked before it is kept: the jar must carry Elle 0.2.8 and exactly one Bifurcan, the
# repaired one. A jar that carries the old Bifurcan would check rw-register histories with the defect
# the fix removed, and a jar with two copies would run whichever the class loader found first.
set -euo pipefail

ELLE_CLI_REPO=https://github.com/ligurio/elle-cli.git
ELLE_CLI_COMMIT=3c2504815945cd4ab1b5cc0142d2fe206e1ee285   # 2026-10-16 "Bump Elle 0.2.7 -> 0.2.8"
ELLE_CLI_VERSION=0.1.11
ELLE_VERSION=0.2.8
BIFURCAN_GROUP=com.aphyr
BIFURCAN_VERSION=0.2.0-rc4
LEIN_IMAGE=clojure:temurin-21-lein-2.13.0-bookworm

DIR="$(cd "$(dirname "$0")" && pwd)"
JAR="$DIR/elle-cli-$ELLE_CLI_VERSION-elle$ELLE_VERSION-standalone.jar"

if [[ -f "$JAR" ]]; then
  echo "already present: $JAR"
  exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "cloning elle-cli at $ELLE_CLI_COMMIT"
git clone -q "$ELLE_CLI_REPO" "$TMP/src"
git -C "$TMP/src" checkout -q "$ELLE_CLI_COMMIT"

if ! grep -q "\[elle \"$ELLE_VERSION\"\]" "$TMP/src/project.clj"; then
  echo "project.clj at $ELLE_CLI_COMMIT does not pin elle $ELLE_VERSION; refusing to build an unknown checker" >&2
  exit 1
fi

# Leiningen writes its dependency cache under $HOME; the container's own home keeps the host clean.
echo "building with $LEIN_IMAGE (downloads the Clojure dependencies; a few minutes)"
docker run --rm -v "$TMP/src:/build" -w /build "$LEIN_IMAGE" lein uberjar

BUILT="$(ls "$TMP"/src/target/elle-cli-*-standalone.jar | head -1)"
if [[ -z "$BUILT" ]]; then
  echo "lein uberjar produced no standalone jar" >&2
  exit 1
fi

elle_version="$(unzip -p "$BUILT" META-INF/maven/elle/elle/pom.properties | sed -n 's/^version=//p')"
bifurcan_version="$(unzip -p "$BUILT" "META-INF/maven/$BIFURCAN_GROUP/bifurcan/pom.properties" 2>/dev/null | sed -n 's/^version=//p' || true)"
bifurcan_copies="$(unzip -l "$BUILT" | grep -c -E 'META-INF/maven/[^/]+/bifurcan/pom.properties' || true)"

if [[ "$elle_version" != "$ELLE_VERSION" ]]; then
  echo "the jar bundles elle $elle_version, not $ELLE_VERSION; refusing it" >&2
  exit 1
fi
if [[ "$bifurcan_version" != "$BIFURCAN_VERSION" ]]; then
  echo "the jar bundles $BIFURCAN_GROUP/bifurcan '$bifurcan_version', not $BIFURCAN_VERSION; refusing it" >&2
  exit 1
fi
if [[ "$bifurcan_copies" != "1" ]]; then
  echo "the jar bundles $bifurcan_copies copies of bifurcan; one is required" >&2
  unzip -l "$BUILT" | grep -E 'META-INF/maven/[^/]+/bifurcan/' >&2 || true
  exit 1
fi

mv "$BUILT" "$JAR"
echo "installed: $JAR (elle $elle_version, $BIFURCAN_GROUP/bifurcan $bifurcan_version)"
