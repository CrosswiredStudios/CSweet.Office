#!/usr/bin/env bash
# Image construction only. Workload VMs remain offline and consume the signed image.
set -euo pipefail
fixtures=${1:?Prepared immutable fixture lock directory is required}
root=/usr/lib/csweet/toolchains/node-typescript
cache="$root/npm-cache"
browsers="$root/browsers"
install -d -m 0755 "$root" "$cache" "$browsers"
export npm_config_cache="$cache"
export PLAYWRIGHT_BROWSERS_PATH="$browsers"
test "$(node --version)" = v24.15.0
test "$(npm --version)" = 11.12.1
shopt -s nullglob
locks=("$fixtures"/*/package-lock.json)
(( ${#locks[@]} > 0 ))
browser_fixture=""
while IFS= read -r spec; do
  [ -z "$spec" ] || npm cache add --ignore-scripts --no-audit --fund=false "$spec"
done < "$fixtures/cache-packages.txt"
for lock in "${locks[@]}"; do
  fixture=${lock%/*}
  (cd "$fixture" && npm ci --ignore-scripts --no-audit --fund=false && npm ci --offline --ignore-scripts --no-audit --fund=false)
  if [ -f "$fixture/node_modules/playwright/cli.js" ]; then browser_fixture="$fixture"; fi
done
test -n "$browser_fixture"
node "$browser_fixture/node_modules/playwright/cli.js" install chromium
node --input-type=module - "$browser_fixture" <<'NODE'
import {createRequire} from 'node:module';
const require=createRequire(process.argv[2]+'/package.json');
const {chromium}=require('playwright');
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
const page=await browser.newPage();
await page.setContent('<canvas id="game" width="64" height="64"></canvas>');
await page.screenshot({path:process.env.PLAYWRIGHT_BROWSERS_PATH+'/image-smoke.png'});
await browser.close();
NODE
# Install the cache and browsers read-only; each VM gets its own writable cache copy.
find "$root" -type d -exec chmod 0755 {} +
find "$cache" -type f -exec chmod 0644 {} +
printf '{"node":"24.15.0","npm":"11.12.1","playwright":"1.62.1","fixtureProviderVersion":"1.0.1"}\n' > "$root/versions.json"
