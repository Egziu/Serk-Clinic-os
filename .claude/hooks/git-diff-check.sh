#!/usr/bin/env sh
# Optional read-only whitespace check. Disabled unless explicitly configured.
# No PHI is read, printed or transmitted by this script; inspect before use.
set -eu
cd "${CLAUDE_PROJECT_DIR:-.}"
if command -v git >/dev/null 2>&1 && git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git diff --check
fi
