#!/bin/bash

# Generate every module's client from the OpenAPI specs in SPEC_DIR.
#
# SPEC_DIR must contain one <module>.yml per module below.
#
# The generator version is pinned so that a generator bump never shows up as a
# spec-looking diff. It is the version the committed code under generated/ was
# produced with, separate from the oapi-codegen runtime version go.mod requires.

set -eu

SPEC_DIR="${SPEC_DIR:?SPEC_DIR is required}"
OAPI_CODEGEN_VERSION="v1.12.4"

MODULES="apilog auth awsmarketplace billing communication integration pricing"

GO_BIN_PATH="$(go env GOBIN)"
if [ -z "${GO_BIN_PATH}" ]; then
    GO_BIN_PATH="$(go env GOPATH)/bin"
fi
# Prepend, not append: another oapi-codegen earlier on PATH would otherwise
# win over the version installed below, defeating the pin.
export PATH="${GO_BIN_PATH}:${PATH}"

go install "github.com/deepmap/oapi-codegen/cmd/oapi-codegen@${OAPI_CODEGEN_VERSION}"

for module in ${MODULES}; do
    sh generate.sh "${module}" "${SPEC_DIR}/${module}.yml"
done
