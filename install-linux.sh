#!/usr/bin/env bash
# VRPC Linux installer: installs the bundled VRPC binary and registers the
# native-messaging host with installed browsers (native + Flatpak). Run `install-linux.sh` to install, `install-linux.sh uninstall`
# to remove. The Discord IPC socket (incl. Flatpak/Snap Discord) is bridged
# by VRPC itself at runtime, so it is not configured here.
set -euo pipefail

APP_NAME="VRPCApp"
APP_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/$APP_NAME"
BIN_DIR="$APP_DIR/bin"
EXE="$BIN_DIR/VRPC"

HOST_NAME="vrpc"
FIREFOX_EXT_ID="vrpc@viktir.com"
# If using dev builds, change CHROME_EXT_ID to the one in browser.
CHROME_EXT_ID="${VRPC_CHROME_EXT_ID:-dmfcnhgakihhigbkpefjgjkbbnkjagpb}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '%s\n' "$*"; }

# Installs the prebuilt VRPC payload into $BIN_DIR (~/.config/VRPCApp/bin).
# Searches in order: $VRPC_PAYLOAD, ./dist, then local publish output (each copied
# whole, so multi-file builds work), and finally a single ./VRPC next to this script.
install_app() {
    local payload_dir="" single="" cand
    for cand in \
        "${VRPC_PAYLOAD:-}" \
        "$SCRIPT_DIR/dist" \
        "$SCRIPT_DIR/publish-linux" \
        "$SCRIPT_DIR/bin/Release/net9.0/linux-x64/publish"; do
        if [ -n "$cand" ] && [ -f "$cand/VRPC" ]; then payload_dir="$cand"; break; fi
    done
    if [ -z "$payload_dir" ] && [ -f "$SCRIPT_DIR/VRPC" ]; then
        single="$SCRIPT_DIR/VRPC"
    fi

    if [ -z "$payload_dir" ] && [ -z "$single" ]; then
        echo "ERROR: no VRPC binary found." >&2
        echo "Place the prebuilt 'VRPC' executable next to this script, or point" >&2
        echo "the Environment Variable VRPC_PAYLOAD at a folder containing it, then re-run." >&2
        exit 1
    fi

    # Wipe bin/ first so an update can't leave stale runtime files behind. Data/ is a
    # sibling of bin/ and is left untouched, so settings + listening history survive.
    rm -rf "$BIN_DIR"
    mkdir -p "$BIN_DIR"
    if [ -n "$payload_dir" ]; then
        cp -rf "$payload_dir"/. "$BIN_DIR"/
        log "Installed app -> $BIN_DIR  (from $payload_dir)"
    else
        cp -f "$single" "$EXE"
        log "Installed app -> $EXE  (from $single)"
    fi
    chmod +x "$EXE"
}

# Writes vrpc.json. $2 = chromium|firefox selects the allow-list field.
write_manifest() {
    local dir="$1" family="$2" host_path="$3" allow
    if [ "$family" = firefox ]; then
        allow="\"allowed_extensions\": [\"$FIREFOX_EXT_ID\"]"
    else
        allow="\"allowed_origins\": [\"chrome-extension://$CHROME_EXT_ID/\"]"
    fi
    cat > "$dir/$HOST_NAME.json" <<EOF
{
  "name": "$HOST_NAME",
  "description": "Transfers Rich Presence data to Discord",
  "path": "$host_path",
  "type": "stdio",
  $allow
}
EOF
}

# Writes a sandbox wrapper that breaks out to the host, and prints its path.
write_wrapper() {
    local dir="$1" wrapper="$1/vrpc-host.sh"
    cat > "$wrapper" <<EOF
#!/bin/sh
exec flatpak-spawn --host "$EXE" "\$@"
EOF
    chmod +x "$wrapper"
    printf '%s' "$wrapper"
}

# install_host <nm_dir> <chromium|firefox> <native|flatpak> [flatpak_app_id]
install_host() {
    local nm_dir="$1" family="$2" kind="$3" app_id="${4:-}" host_path
    mkdir -p "$nm_dir"
    if [ "$kind" = flatpak ]; then
        host_path="$(write_wrapper "$nm_dir")"
        if [ -n "$app_id" ]; then
            flatpak override --user --talk-name=org.freedesktop.Flatpak "$app_id"
            log "  granted $app_id permission to launch host processes"
        fi
    else
        host_path="$EXE"
    fi
    write_manifest "$nm_dir" "$family" "$host_path"
    log "  registered -> $nm_dir/$HOST_NAME.json"
}

flatpak_has() { command -v flatpak >/dev/null 2>&1 && flatpak info "$1" >/dev/null 2>&1; }

register_browsers() {
    local found=0

    if flatpak_has com.brave.Browser; then
        log "Brave (Flatpak) detected"
        install_host "$HOME/.var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser/NativeMessagingHosts" \
            chromium flatpak com.brave.Browser
        found=1
    fi
    if command -v brave-browser >/dev/null 2>&1 || [ -d "$HOME/.config/BraveSoftware/Brave-Browser" ]; then
        log "Brave (native) detected"
        install_host "$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts" chromium native
        found=1
    fi
    if flatpak_has org.mozilla.firefox; then
        log "Firefox (Flatpak) detected"
        install_host "$HOME/.var/app/org.mozilla.firefox/.mozilla/native-messaging-hosts" firefox flatpak org.mozilla.firefox
        found=1
    fi
    if command -v firefox >/dev/null 2>&1 || command -v firefox-esr >/dev/null 2>&1 || [ -d "$HOME/.mozilla" ]; then
        log "Firefox (native) detected"
        install_host "$HOME/.mozilla/native-messaging-hosts" firefox native
        found=1
    fi
    if command -v chromium >/dev/null 2>&1 || command -v chromium-browser >/dev/null 2>&1 || [ -d "$HOME/.config/chromium" ]; then
        log "Chromium (native) detected"
        install_host "$HOME/.config/chromium/NativeMessagingHosts" chromium native
        found=1
    fi
    if command -v google-chrome >/dev/null 2>&1 || command -v google-chrome-stable >/dev/null 2>&1 || [ -d "$HOME/.config/google-chrome" ]; then
        log "Chrome (native) detected"
        install_host "$HOME/.config/google-chrome/NativeMessagingHosts" chromium native
        found=1
    fi

    if [ "$found" = 0 ]; then
        log "No supported browser found. Install Brave/Firefox/Chromium/Chrome and re-run."
        log "Or open an issue on GitHub for your specific browser."
    fi
}

uninstall() {
    log "Removing VRPC native-messaging hosts..."
    local d
    for d in \
        "$HOME/.var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser/NativeMessagingHosts" \
        "$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts" \
        "$HOME/.var/app/org.mozilla.firefox/.mozilla/native-messaging-hosts" \
        "$HOME/.mozilla/native-messaging-hosts" \
        "$HOME/.config/chromium/NativeMessagingHosts" \
        "$HOME/.config/google-chrome/NativeMessagingHosts"; do
        rm -f "$d/$HOST_NAME.json" "$d/vrpc-host.sh"
    done
    local id
    for id in com.brave.Browser org.mozilla.firefox; do
        flatpak_has "$id" && flatpak override --user --no-talk-name=org.freedesktop.Flatpak "$id" 2>/dev/null || true
    done
    rm -rf "$BIN_DIR"
    log "Done. App data in $APP_DIR was left intact (delete it manually to wipe settings/history)."
}

case "${1:-install}" in
    install)
        install_app
        register_browsers
        cat <<EOF

Done. Next steps:
  1. Restart your browser.
  2. Make sure the VRPC extension is installed https://viktir.com/vrpc.
  3. Play something on YouTube Music or Soundcloud; Rich Presence should appear in Discord.

The Discord IPC socket (native, Flatpak, or Snap) is bridged automatically by VRPC.

Notes:
To uninstall use ./install-linux.sh uninstall.
When updating just run ./install-linux.sh again on the new version.
EOF
        ;;
    uninstall) uninstall ;;
    *) echo "usage: $0 [install|uninstall]" >&2; exit 1 ;;
esac
