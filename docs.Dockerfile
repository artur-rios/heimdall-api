# The docs site of heimdall-api (the catalog's heimdall-api-docs): its Hugo site (docs/), built in a pinned Hugo image
# with the API's coverage report, and served by static-web-server. The repository's own `Dockerfile` is the API's, so
# this one is named by the stack file (yggdrasil's stacks/heimdall-api-docs.yml: `dockerfile: docs.Dockerfile`).
# It is built from the templates/site-hugo Dockerfile of yggdrasil, with the three differences marked below.
#
# The coverage report is not in the repository: it is the archive of the `test` check (`docs/coverage-report/**`),
# which the pipeline hands to the deploy as an input (`deploy.inputs: [test]` in yggdrasil.yml) and scripts/deploy.sh
# copies into the build context before this builds. docs/hugo.toml mounts it:
#   [[module.mounts]]
#     source = 'coverage-report'
#     target = 'static/coverage-report'
# so the site serves it at <path>/coverage-report/. The build context is filtered by docs.Dockerfile.dockerignore, which
# BuildKit reads in place of the .dockerignore of the API's image (that one leaves docs/ out).
#
# The build arguments are the stack file's (templates/stacks/site.yml), which scripts/deploy.sh feeds:
#   PUBLIC_HOST, PUBLIC_PATH   the address the site is served at, docs.artur-rios.tech/heimdall-api: Hugo's baseURL is
#                    https://<host><path>/ (just <path>/ when there is no host). The proxy does not strip the path, so
#                    the files are put under it too. Leave the baseURL of docs/hugo.toml alone: this overrides it.
#   DOCS_DIR         the Hugo site's directory (default docs).
#   GIT_COMMIT, YGG_ENVIRONMENT   the commit and the environment this image is built for ({{ getenv
#                    "HUGO_GIT_COMMIT" }} and {{ getenv "HUGO_YGG_ENVIRONMENT" }} in a Hugo template: Hugo's getenv
#                    only allows names that start with HUGO_).
#
# Different from the template:
#   1. The image is the `git` variant of the same Hugo release: docs/hugo.toml has enableGitInfo = true (the pages show
#      when they were last changed), which needs git and the repository's history, so .git is part of the context.
#   2. Docsy 0.17 and later compiles its SCSS with Dart Sass, which Hugo looks up as `sass` on the PATH; the image has
#      none. The standalone release is downloaded, checked against the checksum GitHub publishes for it, and put on
#      the PATH, as the Build Docs workflow did.
#   3. The coverage report must be there: the build stops, saying so, when the deploy did not hand it over.
#
# The theme (Docsy: @docsy/theme in docs/package.json, themesDir = node_modules) is installed with `npm ci` from
# docs/package-lock.json.
FROM hugomods/hugo:debian-node-git-0.165.0@sha256:83cbf887851fea9fa73dc25fd55018deaa302fcb757375057a4cc9bbfc54177d AS build

ARG DART_SASS_VERSION=1.105.1
ADD --checksum=sha256:9046fdd4a31a524a0020298c55d9e155d4c22363ef268e8992574335a72c6a6f \
    https://github.com/sass/dart-sass/releases/download/${DART_SASS_VERSION}/dart-sass-${DART_SASS_VERSION}-linux-x64.tar.gz \
    /tmp/dart-sass.tar.gz
RUN tar -xzf /tmp/dart-sass.tar.gz -C /opt && rm /tmp/dart-sass.tar.gz
ENV PATH="/opt/dart-sass:${PATH}"

WORKDIR /src
COPY . .

ARG DOCS_DIR=docs
RUN test -f "${DOCS_DIR}/coverage-report/index.html" \
    || { echo "docs.Dockerfile: ${DOCS_DIR}/coverage-report/index.html is missing: the deploy hands over the 'test' check's archive (deploy.inputs in yggdrasil.yml), and a build without a deploy has none" >&2; exit 1; }

ARG PUBLIC_HOST=
ARG PUBLIC_PATH=
ARG GIT_COMMIT=
ARG YGG_ENVIRONMENT=
ENV HUGO_GIT_COMMIT=${GIT_COMMIT} \
    HUGO_YGG_ENVIRONMENT=${YGG_ENVIRONMENT}

RUN set -eu; \
    cd "$DOCS_DIR"; \
    if [ -f package-lock.json ]; then npm ci; fi; \
    if [ -n "$PUBLIC_HOST" ]; then base="https://$PUBLIC_HOST$PUBLIC_PATH/"; else base="$PUBLIC_PATH/"; fi; \
    hugo --environment production --minify --gc --baseURL "$base" --destination /out

FROM joseluisq/static-web-server:2.44.1@sha256:94618e3d17802265a21926217e6f9e7832b311b6fbf5933684001739fafed06a

# Declared again: an argument lives in the stage that declares it.
ARG PUBLIC_PATH=
ARG GIT_COMMIT=
ARG YGG_ENVIRONMENT=

# The server has no shell, so nothing is run here: the site is copied under its path, and /healthz is a file of
# its own at the root. Port 8080, a numeric non-root user.
COPY --from=build /out/ /public${PUBLIC_PATH}/
COPY <<EOF /public/healthz
ok
EOF

# Hugo writes 404.html at the site's root (under the path): an address the site does not have is a 404.
ENV SERVER_PORT=8080 \
    SERVER_ROOT=/public \
    SERVER_ERROR_PAGE_404=/public${PUBLIC_PATH}/404.html

LABEL org.opencontainers.image.revision="${GIT_COMMIT}" \
      yggdrasil.built-for="${YGG_ENVIRONMENT}"

USER 65532:65532
EXPOSE 8080
