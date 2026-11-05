#!/usr/bin/env bash
set -euo pipefail

commit=$(git rev-parse 'HEAD^{commit}')
ref="refs/tags/$TAG"
remote_tag=$(git ls-remote --refs origin "$ref")
if [[ -z "$remote_tag" && "$GITHUB_EVENT_NAME" == workflow_dispatch ]]; then
    git push origin "$commit:$ref"
fi

verify_tag() {
    git fetch --quiet --no-tags origin "$ref"
    if [[ "$(git rev-parse 'FETCH_HEAD^{commit}')" != "$commit" ]]; then
        echo "::error::Tag $TAG points to a different commit. Choose a new version."
        exit 1
    fi
}
verify_tag

if draft=$(gh release view "$TAG" --json isDraft --jq .isDraft); then
    if [[ "$draft" != true ]]; then
        echo "::error::Release $TAG is already public. Choose a new version."
        exit 1
    fi
else
    gh release create "$TAG" --verify-tag --target "$commit" --draft --generate-notes --title "TalkToMe $TAG"
fi

# An interrupted upload leaves a draft. A retry replaces its three assets before publishing.
gh release upload "$TAG" assets/TalkToMe-{Windows-x64,macOS-arm64,macOS-x86_64}.zip --clobber
verify_tag
gh release edit "$TAG" --verify-tag --draft=false
