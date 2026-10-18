#!/usr/bin/env sh
# SPDX-License-Identifier: GPL-3.0-only
set -eu
server_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
target_os=${GOOS:-linux}
target_arch=${GOARCH:-amd64}
extension=''
if [ "$target_os" = windows ]; then extension='.exe'; fi
output=${1:-"$server_root/../artifacts/server/$target_os-$target_arch/embeddesk-server$extension"}
(cd "$server_root/www" && npm ci && npm run build)
test -f "$server_root/www/dist/index.html"
mkdir -p "$(dirname -- "$output")"
(cd "$server_root" && GOOS="$target_os" GOARCH="$target_arch" CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o "$output" .)
printf 'Built %s (console and licenses embedded)\n' "$output"
