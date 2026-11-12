#!/usr/bin/env bash
# The end-to-end tests against Chromium in Playwright's Linux image, so every machine renders the
# same pixels and the screenshots in e2e/screenshots/ are compared. The tests and the standalone
# server run here; the browser reaches the server through Playwright's connection. Arguments go to
# `playwright test`, e.g. --update-snapshots=changed to remake the screenshots that changed.
set -euo pipefail
cd "$(dirname "$0")/.."

# The image and the server match the installed @playwright/test, which a client needs.
version=$(pnpm exec playwright --version | awk '{print $2}')
container=$(docker run --detach --rm --init --ipc=host --user pwuser --workdir /home/pwuser \
  --publish 127.0.0.1::3000 "mcr.microsoft.com/playwright:v${version}-noble" \
  npx --yes "playwright@${version}" run-server --port 3000 --host 0.0.0.0)
trap 'docker stop --time 1 "$container" > /dev/null' EXIT
port=$(docker port "$container" 3000/tcp | head -n 1 | sed 's/.*://')
for _ in $(seq 120); do
  curl -fs -o /dev/null "http://127.0.0.1:${port}/" && break
  sleep 1
done
PLAYWRIGHT_BROWSER_SERVER="ws://127.0.0.1:${port}/" pnpm exec playwright test "$@"
