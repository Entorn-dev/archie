#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -eq 0 ]]; then
  echo "Usage: $0 <artifact>..." >&2
  exit 2
fi

if grep -EnH 'Data Source=|synthetic-(password|token)|SYNTHETIC|sourceSnippet|file://|[A-Za-z]:\\\\|/(home|Users|tmp)/' -- "$@"; then
  echo "Generated artifact security audit found secret or local-path material." >&2
  exit 1
else
  status="$?"
  if [[ "$status" -ne 1 ]]; then
    echo "Generated artifact security audit could not inspect every artifact." >&2
    exit "$status"
  fi
fi
