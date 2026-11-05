#!/usr/bin/env bash
# Image construction only. Builds sgrep from the hash-pinned source with a temporary
# Rust toolchain, bakes the embedding models and tree-sitter parsers it needs into
# read-only image paths, proves an unprivileged offline search, then removes Rust,
# CARGO_HOME and the sources. Workload VMs stay offline and consume the signed image.
set -euo pipefail
payload=${1:?Prepared development payload directory is required}
# Validation only: stage under an absolute root instead of /. Image builds leave it unset.
destdir=${CSWEET_SGREP_DESTDIR:-}
[[ -z "$destdir" || "$destdir" == /* ]] || { echo 'CSWEET_SGREP_DESTDIR must be an absolute path.' >&2; exit 2; }
test "$(id -u)" = 0 || { echo 'provision-sgrep.sh must run as root.' >&2; exit 2; }
python=$(command -v python3.12 || command -v python3)
arch=$(tr -d '\r\n' < "$payload/development-architecture")
read -r sgrep_version sgrep_revision rust_version rust_target rust_sha256 source_sha256 < <(
  "$python" -I - "$payload/development-tools.lock.json" "$arch" <<'PYTHON'
import json,sys
lock=json.load(open(sys.argv[1],encoding='utf-8')); s=lock['sgrep']; a=lock['architectures'][sys.argv[2]]
print(s['version'],s['revision'],s['rustVersion'],a['rustTarget'],a['rustSha256'],s['sourceSha256'])
PYTHON
)
test -n "${source_sha256:-}" || { echo 'The sgrep pins are missing from development-tools.lock.json.' >&2; exit 2; }
# provision-development.sh already verified these; re-check so this script is safe on its own.
printf '%s  %s\n' "$rust_sha256" "$payload/rust.tar.xz" "$source_sha256" "$payload/sgrep-source.tar.gz" |
  sha256sum --check --quiet --strict

# Model ids and revisions are compiled into sgrep at the pinned revision; each file is
# size- and SHA-256-checked by sgrep itself before use.
code_model=minishlab/potion-code-16M-v2
code_revision=e9d2a44ca6a05ac6685f3b23709ea57eb7352d5b
text_model=minishlab/potion-base-8M
text_revision=bf8b056651a2c21b8d2565580b8569da283cab23
# Languages sgrep parses with tree-sitter (src/splitter.rs); everything else uses text chunks.
parser_languages='"c","cpp","csharp","go","java","javascript","python","rust","tsx","typescript"'

runtime_root=/usr/lib/csweet/toolchains/sgrep
toolchain="$destdir$runtime_root"
hf_cache="$toolchain/hf-cache"
parsers="$toolchain/parsers"
binary="$destdir/usr/local/bin/sgrep"
query='retry with exponential backoff'

work=$(mktemp -d "${TMPDIR:-/var/tmp}/csweet-sgrep.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
mkdir "$work/rust-dist" "$work/source" "$work/fixture" "$work/home"

tar -xJf "$payload/rust.tar.xz" --strip-components=1 -C "$work/rust-dist"
bash "$work/rust-dist/install.sh" --prefix="$work/rust" --disable-ldconfig \
  --components="rustc,cargo,rust-std-$rust_target" >/dev/null
rm -rf -- "$work/rust-dist"
tar -xzf "$payload/sgrep-source.tar.gz" --strip-components=1 -C "$work/source"
test "$(grep -m 1 '^version = ' "$work/source/Cargo.toml")" = "version = \"$sgrep_version\""
grep -Fq "repo: \"$code_model\"" "$work/source/src/main.rs"
grep -Fq "revision: \"$code_revision\"" "$work/source/src/main.rs"
grep -Fq "repo: \"$text_model\"" "$work/source/src/main.rs"
grep -Fq "revision: \"$text_revision\"" "$work/source/src/main.rs"
language_pack_version=$(grep -A 1 '^name = "tree-sitter-language-pack"$' "$work/source/Cargo.lock" |
  sed -n 's/^version = "\(.*\)"$/\1/p')
test -n "$language_pack_version"

build_path="$work/rust/bin:$PATH"
test "$(env PATH="$build_path" rustc --version | cut -d ' ' -f 2)" = "$rust_version"
test "$(env PATH="$build_path" cargo --version | cut -d ' ' -f 2)" = "$rust_version"
jobs=$(nproc)
(( jobs <= 4 )) || jobs=4
# crates.io downloads need build-time network; Cargo.lock pins every crate checksum.
env PATH="$build_path" CARGO_HOME="$work/cargo-home" CARGO_TARGET_DIR="$work/target" HOME="$work/home" \
  nice -n 10 cargo build --manifest-path "$work/source/Cargo.toml" --release --locked -j "$jobs"
install -d -m 0755 "$destdir/usr/local/bin"
install -o root -g root -m 0755 "$work/target/release/sgrep" "$binary"
test "$("$binary" --version)" = "sgrep $sgrep_version"
rm -rf -- "$work/rust" "$work/cargo-home" "$work/target" "$work/source"

# A polyglot fixture. Code files exceed sgrep's 2048-character chunk size so every parser loads.
emit() {
  local file=$1 header=$2 line=$3 footer=$4 index
  { printf "$header"; for index in $(seq 1 64); do printf "$line" "$index" "$index"; done; printf "$footer"; } >"$work/fixture/$file"
}
emit Retry.cs 'namespace Fixture;\npublic static class Retry\n{\n' '    public static int Backoff%d(int attempt) => attempt * %d;\n' '}\n'
emit retry.ts '' 'export function backoff%d(attempt: number): number { return attempt * %d; }\n' ''
emit panel.tsx "import React from 'react';\n" 'export const Panel%d = () => <div>{%d}</div>;\n' ''
emit retry.js '' 'export function backoff%d(attempt) { return attempt * %d; }\n' ''
emit retry.py '' 'def backoff_%d(attempt):\n    return attempt * %d\n\n' ''
emit retry.go 'package fixture\n\n' 'func Backoff%d(attempt int) int { return attempt * %d }\n' ''
emit retry.rs '' 'pub fn backoff_%d(attempt: u32) -> u32 { attempt * %d }\n' ''
emit Retry.java 'public final class Retry {\n' '    static int backoff%d(int attempt) { return attempt * %d; }\n' '}\n'
emit retry.c '' 'int backoff_%d(int attempt) { return attempt * %d; }\n' ''
emit retry.cpp 'namespace fixture {\n' 'inline int backoff%d(int attempt) { return attempt * %d; }\n' '}\n'
emit retry.sh '#!/bin/sh\n' 'backoff_%d() { echo $(( $1 * %d )); }\n' ''
printf '# Retry policy\n\nFailed requests retry with exponential backoff and jitter.\n' >"$work/fixture/README.md"
printf '{"retry": {"attempts": 5, "backoff": "exponential"}}\n' >"$work/fixture/retry.json"
printf 'retry:\n  attempts: 5\n  backoff: exponential\n' >"$work/fixture/retry.yaml"
printf '<!doctype html>\n<title>Retry</title>\n<p class="retry">Retrying with backoff</p>\n' >"$work/fixture/index.html"
printf '.retry { color: #333; }\n' >"$work/fixture/site.css"
chmod 0755 "$work"
chmod -R a+rX "$work/fixture"

# Warm with network: both models and every parser the fixture needs, without an index.
install -d -m 0755 "$toolchain" "$hf_cache" "$parsers"
for model in code text; do
  env -u HF_HUB_OFFLINE HOME="$work/home" XDG_CACHE_HOME="$work/home/.cache" \
    HF_HUB_CACHE="$hf_cache" TREE_SITTER_LANGUAGE_PACK_CACHE_DIR="$parsers" \
    "$binary" --no-cache --json -n 3 --model "$model" "$query" "$work/fixture" >/dev/null
done
test -d "$hf_cache/models--${code_model//\//--}/snapshots/$code_revision"
test -d "$hf_cache/models--${text_model//\//--}/snapshots/$text_revision"
# The downloaded platform bundle is only needed to extract further parsers; the offline check proves it.
rm -rf -- "$parsers/tree-sitter-language-pack/v$language_pack_version/bundles"
chown -R root:root "$toolchain"
# tree-sitter-language-pack refuses group- or other-writable parser directories.
chmod -R a+rX,go-w "$toolchain"

# Offline as an unprivileged user that cannot write either cache: proves no lock file,
# download or repair is needed at runtime. Only the index directory is writable.
index=$(mktemp -d "$work/index.XXXXXX")
chown nobody "$index"
runuser -u nobody -- test ! -w "$hf_cache"
runuser -u nobody -- test ! -w "$parsers"
offline_search() {
  runuser -u nobody -- env -i PATH=/usr/local/bin:/usr/bin:/bin HF_HUB_OFFLINE=1 \
    HF_HUB_CACHE="$hf_cache" TREE_SITTER_LANGUAGE_PACK_CACHE_DIR="$parsers" SGREP_CACHE_DIR="$index" \
    "$binary" --json -n 3 --model "$1" "$query" "$work/fixture"
}
for run in code code text; do
  if ! offline_search "$run" >"$work/offline.json"; then
    echo "Offline sgrep search ($run model) failed from the read-only image caches. If sgrep now needs" \
      'write access there, agents must copy the caches to scratch; fix the image before shipping.' >&2
    exit 1
  fi
  "$python" -I - "$work/offline.json" <<'PYTHON'
import json,sys
results=json.load(open(sys.argv[1],encoding='utf-8'))
assert isinstance(results,list) and results, 'sgrep returned no JSON results'
for r in results:
  assert isinstance(r['path'],str) and isinstance(r['content'],str), r
  assert isinstance(r['start'],int) and isinstance(r['end'],int) and 1<=r['start']<=r['end'], r
PYTHON
done
test -n "$(find "$index" -type f -name 'index-*.bin' -print -quit)"

printf '{"sgrep":"%s","revision":"%s","rust":"%s","treeSitterLanguagePack":"%s","models":{"code":"%s@%s","text":"%s@%s"},"parsers":[%s]}\n' \
  "$sgrep_version" "$sgrep_revision" "$rust_version" "$language_pack_version" \
  "$code_model" "$code_revision" "$text_model" "$text_revision" "$parser_languages" >"$toolchain/versions.json"
cat >"$toolchain/env" <<ENV
# Runtime environment for /usr/local/bin/sgrep (KEY=VALUE lines, no shell syntax).
# Set these on the sgrep child process. The caches are read-only and need no copy:
# sgrep only reads verified files there. Also set SGREP_CACHE_DIR to writable workload
# scratch (for example /run/csweet/workload/cache/sgrep) or pass --no-cache.
HF_HUB_CACHE=$runtime_root/hf-cache
TREE_SITTER_LANGUAGE_PACK_CACHE_DIR=$runtime_root/parsers
HF_HUB_OFFLINE=1
ENV
chmod 0644 "$toolchain/versions.json" "$toolchain/env"
