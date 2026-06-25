#!/usr/bin/env bash
# Run the AgOpenWeb desktop launcher in place (host + embedded WebView UI).
# --launcher forces the all-in-one launcher window; a bare Linux exe with no flag would
# otherwise start the headless daemon (the appliance default — see Program.cs).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$DIR/app/AgOpenWeb.Desktop" --launcher "$@"
