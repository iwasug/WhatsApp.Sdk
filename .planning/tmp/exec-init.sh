#!/usr/bin/env bash
# gsd execute-phase init helper
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

_GSD_SHIM_NAME="gsd-tools.cjs"
_GSD_RUNTIME_ROOT="${RUNTIME_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
GSD_TOOLS="${_GSD_RUNTIME_ROOT}/gsd-core/bin/${_GSD_SHIM_NAME}"
_gsd_at() { for _p; do if [ -f "$_p" ]; then GSD_TOOLS="$_p"; return 0; fi; done; return 1; }
if _gsd_at "${_GSD_RUNTIME_ROOT}/gsd-core/bin/${_GSD_SHIM_NAME}" "${_GSD_RUNTIME_ROOT}/.claude/gsd-core/bin/${_GSD_SHIM_NAME}" "${_GSD_RUNTIME_ROOT}/.codex/gsd-core/bin/${_GSD_SHIM_NAME}"; then
  gsd_run() { node "$GSD_TOOLS" "$@"; }
elif _gsd_at "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/gsd-core/bin/${_GSD_SHIM_NAME}"; then
  gsd_run() { node "$GSD_TOOLS" "$@"; }
elif command -v gsd_run >/dev/null 2>&1; then
  GSD_TOOLS="$(command -v gsd_run)"
  gsd_run() { "$GSD_TOOLS" "$@"; }
else
  echo "ERROR: gsd-tools.cjs not found" >&2; exit 1
fi

# Stale auto-chain clear (manual invocation, no --auto)
gsd_run query config-set workflow._auto_chain_active false >/dev/null 2>&1 || true

PHASE_ARG="1"
INIT=$(gsd_run query init.execute-phase ${PHASE_ARG})
RC=$?
echo "INIT_RC=$RC"
if [[ "$INIT" == @file:* ]]; then INIT=$(cat "${INIT#@file:}"); fi
echo "$INIT"
