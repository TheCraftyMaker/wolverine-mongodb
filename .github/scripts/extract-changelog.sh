#!/usr/bin/env bash
# Print the CHANGELOG.md section for a given version.
# Usage: extract-changelog.sh <version> [changelog-path]
# Exits non-zero (and prints nothing to stdout) if the section is missing/empty.
set -euo pipefail

version="${1:?usage: extract-changelog.sh <version> [changelog-path]}"
changelog="${2:-CHANGELOG.md}"

section="$(awk -v ver="$version" '
  /^## \[/ {
    header = $0
    sub(/^## \[/, "", header)
    sub(/\].*/, "", header)
    if (capture && header != ver) exit
    if (header == ver) { capture = 1; next }
  }
  capture { print }
' "$changelog")"

# Fail loudly if the section has no non-whitespace content.
if [ -z "${section//[$'\t\r\n ']/}" ]; then
  echo "::error::No CHANGELOG.md section found for version '$version'" >&2
  exit 1
fi

printf '%s\n' "$section"
