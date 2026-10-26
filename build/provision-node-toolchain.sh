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
for recipe in phaser babylon; do
  (cd "$fixtures/$recipe" && npm ci --ignore-scripts --no-audit --fund=false)
done
# This pin is already used by the retained PRISM BREAK source lockfile.
npm cache add --ignore-scripts --no-audit --fund=false phaser@3.90.0 eventemitter3@5.0.4
# Browser revision and download origin come from the integrity-pinned Playwright package.
node "$fixtures/phaser/node_modules/playwright/cli.js" install chromium
# Prove the exact locks can restore with no registry access before publishing an image.
for recipe in phaser babylon; do
  (cd "$fixtures/$recipe" && npm ci --offline --ignore-scripts --no-audit --fund=false)
done
node --input-type=module - "$fixtures/phaser" <<'NODE'
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
