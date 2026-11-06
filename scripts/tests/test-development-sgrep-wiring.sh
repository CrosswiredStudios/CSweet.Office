#!/usr/bin/env bash
# Read-only text check; safe on any host with bash and Python 3.
#   bash scripts/tests/test-development-sgrep-wiring.sh
# It proves the sgrep payload wiring is consistent, not that the image builds: run
# build/toolchains/sgrep/provision-sgrep.sh for that.
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
provision="$root/build/provision-development.sh"
sgrep_script="$root/build/toolchains/sgrep/provision-sgrep.sh"
prepare="$root/scripts/Prepare-CSweetDevelopmentPayload.ps1"
fail() { echo "$1" >&2; exit 1; }
python=
for candidate in python3 python; do
  if "$candidate" -c '' >/dev/null 2>&1; then python=$candidate; break; fi
done
[[ -n "$python" ]] || fail 'Python 3 is required.'
test -f "$sgrep_script" || fail 'build/toolchains/sgrep/provision-sgrep.sh is missing.'

# The image recipe runs the sgrep step after the Node toolchain and from the copied payload tree.
node_line=$(grep -n '^bash "\$payload/provision-node-toolchain.sh" "\$payload/node-toolchain"$' "$provision" | cut -d : -f 1)
sgrep_line=$(grep -n '^bash "\$payload/sgrep-toolchain/provision-sgrep.sh" "\$payload"$' "$provision" | cut -d : -f 1)
[[ -n "$node_line" && -n "$sgrep_line" ]] || fail 'provision-development.sh does not invoke provision-sgrep.sh.'
(( sgrep_line > node_line )) || fail 'provision-sgrep.sh must run after the Node toolchain step.'
grep -Fq "Copy-Item -LiteralPath (Join-Path \$root 'build/toolchains/sgrep') -Destination (Join-Path \$payload 'sgrep-toolchain') -Recurse" "$prepare" ||
  fail 'Prepare-CSweetDevelopmentPayload.ps1 does not stage build/toolchains/sgrep as sgrep-toolchain.'

# Every downloaded build input is hash-checked in the image and named identically by all three files.
for artifact in "rust.tar.xz:\$artifacts.rustUrl:\$artifacts.rustSha256:a['rustSha256']" \
                "sgrep-source.tar.gz:\$lock.sgrep.sourceUrl:\$lock.sgrep.sourceSha256:lock['sgrep']['sourceSha256']"; do
  IFS=: read -r name url hash key <<<"$artifact"
  grep -Fq "@{ Name = '$name'; Url = $url; Hash = $hash; Algorithm = 'SHA256' }" "$prepare" ||
    fail "Prepare-CSweetDevelopmentPayload.ps1 does not download and verify $name."
  grep -Fq "('$name','sha256',$key)" "$provision" || fail "provision-development.sh does not verify $name."
  grep -Fq "\$payload/$name" "$sgrep_script" || fail "provision-sgrep.sh does not consume $name."
done

"$python" -I - "$root/build/development-tools.lock.json" <<'PYTHON'
import json,re,sys
lock=json.load(open(sys.argv[1],encoding='utf-8')); s=lock['sgrep']
sha=re.compile('^[0-9a-f]{64}$')
assert re.fullmatch('[0-9a-f]{40}',s['revision']), 'sgrep revision must be a full commit id'
assert s['sourceUrl']=='https://codeload.github.com/context-dot-dev/sgrep/tar.gz/'+s['revision'], 'source URL does not name the pinned revision'
assert s['repository']=='https://github.com/context-dot-dev/sgrep'
assert sha.match(s['sourceSha256']), 'sgrep source hash is not SHA-256'
assert re.fullmatch(r'\d+\.\d+\.\d+',s['version']) and re.fullmatch(r'\d+\.\d+\.\d+',s['rustVersion'])
targets={'x64':'x86_64-unknown-linux-gnu','arm64':'aarch64-unknown-linux-gnu'}
for arch,values in lock['architectures'].items():
  target=targets[arch]
  assert values['rustTarget']==target, arch
  assert values['rustUrl']==f"https://static.rust-lang.org/dist/rust-{s['rustVersion']}-{target}.tar.xz", arch
  assert sha.match(values['rustSha256']), arch
PYTHON
echo 'sgrep payload wiring is consistent across the lock, Prepare script and provisioning scripts.'
