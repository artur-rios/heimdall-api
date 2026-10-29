#!/bin/sh
# The `docker` check: the API's container image (the Dockerfile) can still be produced. Nothing else builds it, and it
# can break on its own: the Dockerfile restores from a hand-listed set of project files rather than the whole tree, so
# a new file that restore depends on (Directory.Packages.props, when central package management was introduced) is
# missing from the build context and fails there while every other check stays green.
#
# Build only, no push and no run: the API inside it is covered by the `test` check. yggdrasil.yml runs it in the
# pinned docker CLI image (an Alpine one: no bash, hence sh) with the CI engine's socket (`docker: true`), from the
# repository's root. The image is not kept: the engine's weekly prune removes it.
set -eu

# The repository's root, wherever this is run from.
cd "$(dirname "$0")/.."

docker build --file Dockerfile --tag heimdall-api-check:ci .
