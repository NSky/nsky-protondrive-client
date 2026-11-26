#!/bin/sh
# Prints the GitHub release notes for a version: its section of CHANGELOG.md, a link to the full list of changes
# since the previous version, and the notice.
#
#   packaging/release-notes.sh <version> [changelog] [repository]
#
# Fails if the changelog has no section for the version, or if the section is still marked "Unreleased": date the
# heading (## [1.2.3] - 2026-12-01) before tagging.
set -eu

version=${1:?usage: release-notes.sh <version> [changelog] [repository]}
changelog=${2:-CHANGELOG.md}
repository=${3:-${GITHUB_REPOSITORY:-NSky/protondrive-client}}

heading=$(grep -m1 -F "## [$version]" "$changelog" || true)
if [ -z "$heading" ]; then
    echo "error: $changelog has no section for $version (## [$version] - <date>)" >&2
    exit 1
fi

case "$heading" in
    *Unreleased*)
        echo "error: the $version section of $changelog is still marked Unreleased; date it before tagging" >&2
        exit 1
        ;;
esac

# The section's body: from after its heading up to the next version heading, without leading or trailing blank
# lines and without the link definitions at the end of the file.
body=$(awk -v heading="$heading" '
    $0 == heading { inside = 1; next }
    inside && /^## \[/ { exit }
    inside && /^\[[^]]+\]: / { exit }
    inside { print }
' "$changelog" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')

if [ -z "$body" ]; then
    echo "error: the $version section of $changelog is empty" >&2
    exit 1
fi

previous=$(grep -E '^## \[[0-9]' "$changelog" | grep -A1 -F "## [$version]" | sed -n '2s/^## \[\([^]]*\)\].*/\1/p')

printf '%s\n\n' "$body"
if [ -n "$previous" ]; then
    printf '**Full list of changes:** https://github.com/%s/compare/v%s...v%s\n\n' "$repository" "$previous" "$version"
fi
printf '%s\n' "---" "" \
    "Beta release, provided as is, without warranty. Unofficial; not affiliated with Proton AG. See the README's Install section and Disclaimer."
