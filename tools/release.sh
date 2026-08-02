#!/usr/bin/env bash
# One-command release from Linux/macOS/WSL: bumps version, commits, tags, pushes.
# CI then builds on Windows, publishes the GitHub release, and submits the
# winget update PR. Mirror of release.ps1 minus the local sanity build
# (compilation needs Windows; the CI build job is the gate).
#
#   ./tools/release.sh 1.0.1
set -euo pipefail

VERSION="${1:-}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: $0 <major.minor.patch>" >&2; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

[[ -z "$(git status --porcelain)" ]] || { echo "Working tree not clean - commit or stash first." >&2; exit 1; }

# bump assembly version in source
sed -i.bak -E \
  -e "s/AssemblyVersion\(\"[0-9.]+\"\)/AssemblyVersion(\"$VERSION.0\")/" \
  -e "s/AssemblyFileVersion\(\"[0-9.]+\"\)/AssemblyFileVersion(\"$VERSION.0\")/" \
  src/WattWidget.cs
rm -f src/WattWidget.cs.bak

# keep the reference manifests in packaging/ current (CI's wingetcreate
# generates the authoritative ones)
for f in packaging/winget/*.yaml; do
  sed -i.bak -E \
    -e "s/PackageVersion: [0-9.]+/PackageVersion: $VERSION/" \
    -e "s#download/v[0-9.]+/WattAWidget-[0-9.]+-win-x64\.zip#download/v$VERSION/WattAWidget-$VERSION-win-x64.zip#" \
    -e "s/InstallerSha256: .+/InstallerSha256: <FILLED-BY-CI>/" \
    "$f"
  rm -f "$f.bak"
done

git add src/WattWidget.cs packaging
git commit -m "Release $VERSION"
git tag "v$VERSION"
git push
git push origin "v$VERSION"

echo
echo "v$VERSION tagged and pushed."
echo "CI will now: build -> publish GitHub release -> submit winget PR."
echo "Watch: https://github.com/pyvinyas/WattAWidget/actions"
