#!/usr/bin/env bash
#
# Install this plugin into a local XIV on Mac setup.
#
# Autofate ships through a custom plugin repository, but a build from this
# working tree is not in it, so it is installed as a dalamud dev plugin: the
# build output is copied next to the game's own data and the copied assembly is
# registered as a dev plugin load location.
#
# Two things about that registration are easy to get wrong, and both fail with
# dalamud reporting that the path does not exist:
#
#   - the path must name the assembly, not the folder holding it. Dalamud tests
#     it with FileInfo.Exists, which is false for a directory.
#   - DevMode has to be on. Dev plugin locations are only scanned when it is,
#     so an otherwise perfect registration is simply skipped.
#
# The copy is a precaution rather than a diagnosed requirement: XIV on Mac is an
# App Sandboxed application holding only files.user-selected.read-only, so a
# repository in your home directory may well be unreadable by the game, while
# everything under the XIV on Mac data directory is demonstrably reachable.
#
# The game runs under wine, where / is mounted as Z:, so the path handed to
# dalamud is the windows shaped one.
#
# The build needs dalamud's dev libraries. XIV on Mac keeps a copy under its own
# data directory, so that is used by default and there is no need for the
# ~/.xlcore layout the csproj assumes. Override with DALAMUD_LIB=/some/path/.
#
#   ./scripts/install.sh              build Debug and install
#   ./scripts/install.sh --release    build Release and install
#   ./scripts/install.sh --no-build   install whatever is already built
#   ./scripts/install.sh --dry-run    print what would happen, change nothing
#   ./scripts/install.sh --status     show what is built and what is installed
#   ./scripts/install.sh --uninstall  remove the registration and the copy
#
# Override the setup location with XOM_ROOT=/some/path.

set -euo pipefail

PLUGIN="Autofate"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
XOM_ROOT="${XOM_ROOT:-$HOME/Library/Application Support/XIV on Mac}"

CONFIG="Debug"
ACTION="install"
DRY=0
BUILD=1
FORCE=0

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --release) CONFIG="Release" ;;
        --debug) CONFIG="Debug" ;;
        --no-build) BUILD=0 ;;
        --dry-run) DRY=1 ;;
        --status) ACTION="status" ;;
        --uninstall) ACTION="uninstall" ;;
        --force) FORCE=1 ;;
        -h|--help) awk 'NR>1 && !/^#/ {exit} NR>1 {sub(/^# ?/, ""); print}' "$0"; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
    shift
done

# The csproj pins Platform to x64, so the output lands one level deeper than the
# usual bin/<config>.
BUILD_DIR="$REPO_ROOT/$PLUGIN/bin/x64/$CONFIG"
INSTALL_DIR="$XOM_ROOT/devPlugins/$PLUGIN"
DALAMUD_CONFIG="$XOM_ROOT/dalamudConfig.json"
# Both the backup and the "is this ours?" test are named after the plugin, so a
# sibling plugin installed by its own copy of this script cannot be mistaken for
# this one, nor have its backup overwritten.
MARKER="$(printf '%s' "$PLUGIN" | tr '[:upper:]' '[:lower:]')"
BACKUP="$XOM_ROOT/dalamudConfig.json.$MARKER-backup"
PLUGIN_CONFIG_DIR="$XOM_ROOT/pluginConfigs/$PLUGIN"

# Dalamud's dev libraries to compile against. The game's own copy is right here
# and always matches the dalamud that will load the result.
DALAMUD_LIB="${DALAMUD_LIB:-}"
if [ -z "$DALAMUD_LIB" ]; then
    if [ -f "$XOM_ROOT/dalamud/Hooks/dev/Dalamud.dll" ]; then
        DALAMUD_LIB="$XOM_ROOT/dalamud/Hooks/dev/"
    elif [ -f "$HOME/.xlcore/dalamud/Hooks/dev/Dalamud.dll" ]; then
        DALAMUD_LIB="$HOME/.xlcore/dalamud/Hooks/dev/"
    fi
fi

[ -d "$XOM_ROOT" ] || die "XIV on Mac setup not found at: $XOM_ROOT (set XOM_ROOT to override)"
[ -f "$DALAMUD_CONFIG" ] || die "no dalamud config at: $DALAMUD_CONFIG"

# dalamud holds its configuration in memory and writes the whole file out when
# the game exits, so anything edited underneath a running game is thrown away.
assert_game_stopped() {
    [ "$DRY" -eq 1 ] && return 0
    if pgrep -f "ffxiv_dx11" >/dev/null 2>&1; then
        [ "$FORCE" -eq 1 ] || die "FFXIV looks like it is running - quit the game first (or pass --force)"
        info "warning: FFXIV appears to be running, dalamud will overwrite this on exit"
    fi
}

# / is mounted as Z: inside the wine prefix the game runs in
windows_path() {
    printf 'Z:%s' "$(printf '%s' "$1" | tr '/' '\\')"
}

# Reads or edits DevPluginLoadLocations. The list is serialised by Newtonsoft
# with $type and $values wrappers, so both that shape and a plain array are
# handled.
config_tool() {
    python3 - "$DALAMUD_CONFIG" "$(windows_path "$INSTALL_DIR/$PLUGIN.dll")" "$1" "$MARKER" <<'PYTHON'
import json
import pathlib
import sys

config_path, target, action = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
marker = sys.argv[4]
LIST_TYPE = (
    "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]],"
    " System.Private.CoreLib"
)
ENTRY_TYPE = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud"
SETTINGS_TYPE = (
    "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],"
    "[Dalamud.Configuration.Internal.DevPluginSettings, Dalamud]], System.Private.CoreLib"
)
ENTRY_SETTINGS_TYPE = "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud"

config = json.loads(config_path.read_text())
node = config.get("DevPluginLoadLocations")

if isinstance(node, dict):
    values = node.setdefault("$values", [])
elif isinstance(node, list):
    values = node
else:
    node = config["DevPluginLoadLocations"] = {"$type": LIST_TYPE, "$values": []}
    values = node["$values"]

def path_of(entry):
    return entry.get("Path", "").rstrip("\\") if isinstance(entry, dict) else ""


def is_target(entry):
    return path_of(entry) == target.rstrip("\\")


# Any earlier registration of this plugin, wherever it pointed. Installing has
# moved location before, and leaving a stale entry behind means dalamud logs an
# error about a path that no longer matters on every startup.
def is_ours(entry):
    return marker in path_of(entry).lower()


def save():
    if isinstance(node, dict):
        node["$values"] = values
    else:
        config["DevPluginLoadLocations"] = values
    config_path.write_text(json.dumps(config, indent=2))


def registered_and_enabled():
    return config.get("DevMode") and any(
        is_target(entry) and entry.get("IsEnabled", True) for entry in values
    )


# Copying a new assembly over a plugin that is already registered needs no
# configuration change at all, and dalamud picks it up on its own when automatic
# reloading is on. That is the difference between an install that can happen
# mid-session and one that has to wait for the game to close.
if action == "needs-change":
    print("no" if registered_and_enabled() else "yes")
    sys.exit(0)

if action == "status":
    dev_mode = "" if config.get("DevMode") else ", but DevMode is off so it will be skipped"
    for entry in values:
        if is_target(entry):
            print(("enabled" if entry.get("IsEnabled", True) else "disabled") + dev_mode)
            break
    else:
        stale = sum(1 for entry in values if is_ours(entry))
        print(f"absent ({stale} stale entr{'y' if stale == 1 else 'ies'})" if stale else "absent")
    sys.exit(0)

if action == "add":
    stale = [entry for entry in values if is_ours(entry) and not is_target(entry)]
    values[:] = [entry for entry in values if entry not in stale]

    notes = []
    if stale:
        notes.append(f"cleared {len(stale)} stale")

    # Start with the plugin loading on boot and reloading when the assembly
    # changes, so installing over a running game is enough. Existing settings
    # are left alone; they are the user's choice, not ours.
    settings = config.setdefault(
        "DevPluginSettings",
        {"$type": SETTINGS_TYPE, },
    )
    if isinstance(settings, dict) and target not in settings:
        settings[target] = {
            "$type": ENTRY_SETTINGS_TYPE,
            "StartOnBoot": True,
            "NotifyForErrors": True,
            "AutomaticReloading": True,
        }
        notes.append("enabled automatic reloading")
    # Dev plugin locations are only scanned when DevMode is on, so registering
    # without it produces a correct entry that is silently never looked at.
    if not config.get("DevMode"):
        config["DevMode"] = True
        notes.append("turned DevMode on")

    for entry in values:
        if is_target(entry):
            already = entry.get("IsEnabled", True) and not notes
            entry["IsEnabled"] = True
            save()
            print("already registered" if already else f"registered ({', '.join(notes)})" if notes else "registered")
            sys.exit(0)

    values.append({"$type": ENTRY_TYPE, "Path": target, "IsEnabled": True})
    save()
    print(f"registered ({', '.join(notes)})" if notes else "registered")
    sys.exit(0)

if action == "remove":
    remaining = [entry for entry in values if not is_ours(entry)]
    if len(remaining) == len(values):
        print("not registered")
        sys.exit(0)
    values[:] = remaining

    note = ""
    # Only give DevMode back if nothing else is relying on it.
    if not remaining and config.get("DevMode"):
        config["DevMode"] = False
        note = " (turned DevMode back off)"
    save()
    print("removed" + note)
PYTHON
}

backup_once() {
    if [ ! -f "$BACKUP" ]; then
        info "backing up dalamud config to $(basename "$BACKUP")"
        [ "$DRY" -eq 1 ] || cp "$DALAMUD_CONFIG" "$BACKUP"
    fi
}

do_status() {
    info "setup:      $XOM_ROOT"
    info "dalamud:    ${DALAMUD_LIB:-(not found, the build will use the csproj default)}"
    info "build dir:  $BUILD_DIR"
    if [ -f "$BUILD_DIR/$PLUGIN.dll" ]; then
        info "  built:    $(date -r "$BUILD_DIR/$PLUGIN.dll" '+%Y-%m-%d %H:%M')"
        [ -f "$BUILD_DIR/$PLUGIN.json" ] || info "  warning:  no $PLUGIN.json beside the assembly, dalamud will not load it"
    else
        info "  built:    (nothing built yet)"
    fi
    info "installed:  $INSTALL_DIR"
    if [ -f "$INSTALL_DIR/$PLUGIN.dll" ]; then
        info "  copied:   $(date -r "$INSTALL_DIR/$PLUGIN.dll" '+%Y-%m-%d %H:%M')"
    else
        info "  copied:   (nothing installed yet)"
    fi
    info "dev plugin: $(config_tool status)"
    info "  path:     $(windows_path "$INSTALL_DIR/$PLUGIN.dll")"
    info "config:     $PLUGIN_CONFIG_DIR"
}

do_install() {
    # Copying over an already registered plugin touches no configuration, and
    # dalamud reloads it by itself. Only a registration change has to wait for
    # the game to close, because dalamud rewrites the config on exit.
    local needs_change
    needs_change="$(config_tool needs-change)"
    [ "$needs_change" = "no" ] || assert_game_stopped

    if [ "$BUILD" -eq 1 ]; then
        # ECommons is built from source as a submodule, and a fresh clone does
        # not have it, which otherwise fails as a missing project reference.
        [ -f "$REPO_ROOT/ECommons/ECommons/ECommons.csproj" ] \
            || die "ECommons submodule is empty - run: git submodule update --init --recursive"

        local build_args=()
        [ -n "$DALAMUD_LIB" ] && build_args+=("-p:DalamudLibPath=$DALAMUD_LIB")

        info "building $CONFIG..."
        if [ "$DRY" -eq 1 ]; then
            info "  would: dotnet build $REPO_ROOT/$PLUGIN/$PLUGIN.csproj -c $CONFIG ${build_args[*]-}"
        else
            dotnet build "$REPO_ROOT/$PLUGIN/$PLUGIN.csproj" -c "$CONFIG" -v q --nologo \
                ${build_args[@]+"${build_args[@]}"}
        fi
    fi

    if [ "$DRY" -eq 0 ]; then
        [ -f "$BUILD_DIR/$PLUGIN.dll" ] || die "no build output at $BUILD_DIR/$PLUGIN.dll"
        [ -f "$BUILD_DIR/$PLUGIN.json" ] || die "no manifest at $BUILD_DIR/$PLUGIN.json"
    fi

    if [ "$DRY" -eq 1 ]; then
        info "  would: copy the build to $INSTALL_DIR"
    else
        info "installing -> $INSTALL_DIR"
        mkdir -p "$INSTALL_DIR"
        # clear first so files dropped between builds do not linger
        find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        cp -R "$BUILD_DIR"/. "$INSTALL_DIR/"
        # dalamud reads a dev plugin's icon from images/icon.png beside the
        # assembly, and the packager leaves it out of the plain build output.
        [ -d "$REPO_ROOT/$PLUGIN/images" ] && cp -R "$REPO_ROOT/$PLUGIN/images" "$INSTALL_DIR/"
    fi

    if [ "$needs_change" = "no" ]; then
        info "dev plugin: already registered, left the config alone"
        info ""
        info "done. dalamud reloads the plugin on its own if automatic reloading is on,"
        info "otherwise hit reload in the dev plugins tab."
        return
    fi

    backup_once

    if [ "$DRY" -eq 1 ]; then
        info "  would: register $(windows_path "$INSTALL_DIR/$PLUGIN.dll") as a dev plugin"
    else
        info "dev plugin: $(config_tool add)"
    fi

    info ""
    info "done. start the game and run /af."
    info "settings will land in: $PLUGIN_CONFIG_DIR"
    info "note: once registered, installing again works with the game running."
}

do_uninstall() {
    assert_game_stopped
    backup_once
    if [ "$DRY" -eq 1 ]; then
        info "  would: remove the dev plugin registration and $INSTALL_DIR"
    else
        info "dev plugin: $(config_tool remove)"
        [ -d "$INSTALL_DIR" ] && rm -rf "$INSTALL_DIR" && info "removed $INSTALL_DIR"
    fi
}

case "$ACTION" in
    status) do_status ;;
    install) do_install ;;
    uninstall) do_uninstall ;;
esac
