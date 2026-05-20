#!/usr/bin/env bash
set -euo pipefail

# Compute image tags and app version and write them to $GITHUB_OUTPUT

if [[ "${GITHUB_REF:-}" =~ ^refs/tags/v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
  VERSION="${BASH_REMATCH[1]}"
  MAJOR="${VERSION%%.*}"
  MINOR="${VERSION#*.}"
  MINOR="${MINOR%%.*}"

  TAGS_LIST=("v${VERSION}" "${VERSION}" "${MAJOR}.${MINOR}" "${MAJOR}" "latest")
  APP_VERSION="${VERSION}"
else
  BRANCH="${GITHUB_REF#refs/heads/}"
  SANITIZED_BRANCH="$(echo "${BRANCH}" | sed -E 's#[^A-Za-z0-9_.-]+#-#g' | tr '[:upper:]' '[:lower:]')"
  SHORT_SHA="${GITHUB_SHA:0:7}"

  TAGS_LIST=("${SANITIZED_BRANCH}" "${SANITIZED_BRANCH}-build${GITHUB_RUN_NUMBER}" "sha-${SHORT_SHA}" "preview")
  APP_VERSION="prerelease-${GITHUB_RUN_NUMBER}"
fi

echo "app_version=${APP_VERSION}" >> "${GITHUB_OUTPUT}"

echo "image_tags<<EOF" >> "${GITHUB_OUTPUT}"
for t in "${TAGS_LIST[@]}"; do
  printf '%s\n' "$t" >> "${GITHUB_OUTPUT}"
done
echo "EOF" >> "${GITHUB_OUTPUT}"

echo "full_tags<<EOF" >> "${GITHUB_OUTPUT}"
# Lowercase the image base names to satisfy registry requirements
DOCKERHUB_IMAGE_LC="$(echo "${DOCKERHUB_IMAGE:-}" | tr '[:upper:]' '[:lower:]')"
GHCR_IMAGE_LC="$(echo "${GHCR_IMAGE:-}" | tr '[:upper:]' '[:lower:]')"

for t in "${TAGS_LIST[@]}"; do
  printf '%s\n' "${DOCKERHUB_IMAGE_LC}:${t}" >> "${GITHUB_OUTPUT}"
  printf '%s\n' "${GHCR_IMAGE_LC}:${t}" >> "${GITHUB_OUTPUT}"
done
echo "EOF" >> "${GITHUB_OUTPUT}"

exit 0
