#!/usr/bin/env bash
# Builds the WPF app with the Windows .NET SDK and installs it to a Windows-local folder.
#
# The source lives in WSL but MSBuild and the app itself must run on Windows, so we hand
# MSBuild the \\wsl.localhost UNC path. The *output* has to land on a real Windows drive:
# Windows refuses to launch an executable from a network location without a prompt.
set -euo pipefail

CONFIG="${1:-Release}"
DISTRO="${WSL_DISTRO_NAME:-Ubuntu}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_UNC="\\\\wsl.localhost\\${DISTRO}$(printf '%s' "$HERE" | tr '/' '\\')\\ClaudeSessions.csproj"

echo "Building $CONFIG from $SRC_UNC"

powershell.exe -NoProfile -Command "
  \$dest = Join-Path \$env:LOCALAPPDATA 'ClaudeSessions'
  # A running instance holds a lock on its own .exe and the copy step fails.
  \$live = Get-Process ClaudeSessions -ErrorAction SilentlyContinue
  if (\$live) { Write-Host 'Closing running instance…'; \$live | Stop-Process -Force; Start-Sleep -Milliseconds 400 }
  dotnet build '$SRC_UNC' -c $CONFIG -o \$dest --nologo -v minimal
  if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
  Write-Host ''
  Write-Host \"Installed to \$dest\"
  Write-Host \"Run it with:  powershell.exe -c \"\"Start-Process '\$dest\ClaudeSessions.exe'\"\"\"
"
