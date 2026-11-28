#!/usr/bin/env bash
# Shared steps of the workflows that build Proton's .NET packages from Proton's own repositories and publish them to
# this repository owner's GitHub Packages feed (proton-cryptography.yml, proton-drive-sdk.yml).
#
#   proton-package.sh versions ID              Published versions of package ID, oldest first (none: no output)
#   proton-package.sh built-from ID VERSION    The upstream commit that version was built from (none: no output)
#   proton-package.sh sort-versions            Sort versions read from stdin, oldest first, pre-releases first
#   proton-package.sh nuget-config             Write a nuget.config that takes Proton.* packages from the feed
#   proton-package.sh pack PROJECT [ARG...]    dotnet pack into out/, recording the upstream repository and commit
#   proton-package.sh verify NUPKG [FILE...]   Check that a package records the upstream commit and has these files
#   proton-package.sh push NUPKG...            Push packages to the feed (versions already there are skipped)
#
# Environment: GH_TOKEN (a token that can read, and for push write, the owner's packages), GITHUB_REPOSITORY_OWNER,
# UPSTREAM (owner/name of Proton's repository); for pack and verify also VERSION and UPSTREAM_SHA, and optionally
# UPSTREAM_REF (a tag or branch name, shown in the release notes). The GITHUB_* variables come from Actions.
set -euo pipefail

feed_url() {
  echo "https://nuget.pkg.github.com/${GITHUB_REPOSITORY_OWNER}"
}

# NuGet's flat container: lower-case package IDs.
package_url() {
  echo "$(feed_url)/download/${1,,}"
}

sort_versions() {
  # sort -V puts 1.0.0-beta after 1.0.0; '~' sorts before everything, so it orders pre-releases first.
  sed 's/-/~/' | sort -V | sed 's/~/-/'
}

versions() {
  local id=$1 body status
  body=$(mktemp)
  status=$(curl -sS -o "${body}" -w '%{http_code}' -u "x:${GH_TOKEN}" "$(package_url "${id}")/index.json")
  case "${status}" in
    200) jq -r '.versions[]' "${body}" | sort_versions ;;
    404) ;; # Not published yet.
    *)
      echo "::error::Reading the published versions of ${id} failed (HTTP ${status}). Does this repository have access to the package (package settings, Manage Actions access)?" >&2
      exit 1
      ;;
  esac
  rm -f "${body}"
}

# Packages record the upstream commit in their release notes. Older ones, built from a fork, only have the fork's
# commit in <repository commit="...">; it is returned instead, and a compare with upstream still works on it.
built_from() {
  local id=$1 version=$2 nupkg nuspec sha
  nupkg=$(mktemp)
  curl -fsSL -o "${nupkg}" -u "x:${GH_TOKEN}" "$(package_url "${id}")/${version}/${id,,}.${version}.nupkg"
  nuspec=$(unzip -p "${nupkg}" '*.nuspec')
  rm -f "${nupkg}"
  sha=$(grep -oE "github\.com/${UPSTREAM}/commit/[0-9a-f]{40}" <<< "${nuspec}" | head -n 1 | grep -oE '[0-9a-f]{40}$' || true)
  if [ -z "${sha}" ]; then
    sha=$(grep -oE 'commit="[0-9a-f]{40}"' <<< "${nuspec}" | head -n 1 | grep -oE '[0-9a-f]{40}' || true)
  fi
  echo "${sha}"
}

# Replaces the repository's nuget.config: Proton's points at their internal feed. The token is read from the
# environment at restore time (NUGET_FEED_TOKEN), so it is not written to disk.
nuget_config() {
  cat > nuget.config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="Proton" value="$(feed_url)/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <Proton>
      <add key="Username" value="${GITHUB_REPOSITORY_OWNER}" />
      <add key="ClearTextPassword" value="%NUGET_FEED_TOKEN%" />
    </Proton>
  </packageSourceCredentials>
  <packageSourceMapping>
    <packageSource key="Proton">
      <package pattern="Proton.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
}

# The repository fields name Proton's sources; the release notes name the same commit (which built-from reads back)
# and the workflow run that built the package.
pack() {
  local project=$1
  shift
  local source="https://github.com/${UPSTREAM}/commit/${UPSTREAM_SHA}"
  if [ -n "${UPSTREAM_REF:-}" ] && [ "${UPSTREAM_REF}" != "${UPSTREAM_SHA}" ]; then
    source="${source} (${UPSTREAM_REF})"
  fi
  dotnet pack "${project}" -c Release --output out \
    -p:Version="${VERSION}" \
    -p:RepositoryUrl="https://github.com/${UPSTREAM}" \
    -p:RepositoryType=git \
    -p:RepositoryCommit="${UPSTREAM_SHA}" \
    -p:PackageReleaseNotes="Built from unchanged sources: ${source}. Build: ${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" \
    "$@"
}

verify() {
  local nupkg=$1 file nuspec
  shift
  [ -f "${nupkg}" ] || { echo "::error::${nupkg} was not built"; exit 1; }
  unzip -l "${nupkg}"
  for file in "$@"; do
    unzip -l "${nupkg}" "${file}" > /dev/null || { echo "::error::${file} is missing from ${nupkg}"; exit 1; }
  done
  nuspec=$(unzip -p "${nupkg}" '*.nuspec')
  grep -qF "<version>${VERSION}</version>" <<< "${nuspec}" \
    || { echo "::error::${nupkg} does not have version ${VERSION}"; exit 1; }
  grep -qF "url=\"https://github.com/${UPSTREAM}\"" <<< "${nuspec}" \
    || { echo "::error::${nupkg} does not name ${UPSTREAM} as its repository"; exit 1; }
  grep -qF "github.com/${UPSTREAM}/commit/${UPSTREAM_SHA}" <<< "${nuspec}" \
    || { echo "::error::${nupkg} does not record the upstream commit"; exit 1; }
}

push() {
  local nupkg
  for nupkg in "$@"; do
    dotnet nuget push "${nupkg}" \
      --source "$(feed_url)/index.json" \
      --api-key "${GH_TOKEN}" \
      --no-symbols \
      --skip-duplicate
    echo "- Published **$(basename "${nupkg}" .nupkg)**" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  done
}

command=${1:-}
shift || true
case "${command}" in
  versions) versions "$@" ;;
  built-from) built_from "$@" ;;
  sort-versions) sort_versions ;;
  nuget-config) nuget_config ;;
  pack) pack "$@" ;;
  verify) verify "$@" ;;
  push) push "$@" ;;
  *)
    sed -n '2,16s/^# \{0,1\}//p' "$0" >&2
    exit 2
    ;;
esac
