#!/bin/bash
# Claude Code on the web: install the .NET 10 SDK (if missing) and restore packages and local tools,
# so `dotnet build`, `dotnet test`, `dotnet format` and Stryker work from the first command.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR="$HOME/.dotnet"
export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$PATH"
export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  installer="$(mktemp)"
  curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --channel 10.0 --install-dir "$DOTNET_DIR" >/dev/null
  rm -f "$installer"
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -q 'DOTNET_ROOT=' "$CLAUDE_ENV_FILE" 2>/dev/null; then
  {
    echo "export PATH=\"$DOTNET_DIR:$DOTNET_DIR/tools:\$PATH\""
    echo "export DOTNET_ROOT=\"$DOTNET_DIR\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"
dotnet restore Milligram.slnx
dotnet tool restore
