#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
rm -rf "$repo_dir/src/bin" "$repo_dir/src/obj" "$repo_dir/tests/bin" "$repo_dir/tests/obj"
