#!/usr/bin/env bash
# Package a built XivArcade plugin for Dalamud, and write the one-entry plugin
# repository listing that spacegho.st/mods/ffxiv/plugins.json is assembled from.
# Publishes nothing: the release workflow attaches what this writes.
#
#   dotnet build XivArcade.slnx -c Release && tools/package.sh
#
# Output, all under $OUT (default ../xiv-arcade-build/release):
#   latest.zip                  the plugin folder as Dalamud installs it (XivArcade.json,
#                               XivArcade.dll, XivArcade.Core.dll, no .pdb). The name is stable on purpose, so
#                               .../releases/latest/download/latest.zip never changes.
#   XivArcade-<version>.zip       the same bytes under a self-describing name
#   pluginmaster.json           stable-channel listing (one entry, a JSON array)
#   pluginmaster-testing.json   testing-channel listing, when TESTING=1
#
# Environment:
#   BIN                the build output to package (default
#                      $XIVARCADE_ARTIFACTS/bin/XivArcade.Plugin/release, else
#                      ../xiv-arcade-build/artifacts/bin/XivArcade.Plugin/release)
#   OUT                where to write (default ../xiv-arcade-build/release)
#   REPO               owner/name on GitHub (default $GITHUB_REPOSITORY, else
#                      Spaceghost/xivarcade-dalamud)
#   TESTING            1 to write pluginmaster-testing.json instead of pluginmaster.json
#   RELEASE_TAG        the tag being released; the listing then carries the changelog
#   SOURCE_DATE_EPOCH  timestamp for the zip entries and LastUpdate (default: the last
#                      commit's time, else 0), so the same build packs to the same bytes
#
# Exit codes: 0 done, 1 nothing to package or a missing file, 127 a missing tool.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

command -v zip >/dev/null || { echo "error: zip is required" >&2; exit 127; }
command -v python3 >/dev/null || { echo "error: python3 is required" >&2; exit 127; }

ARTIFACTS="${XIVARCADE_ARTIFACTS:-$ROOT/../xiv-arcade-build/artifacts}"
BIN="${BIN:-$ARTIFACTS/bin/XivArcade.Plugin/release}"
OUT="${OUT:-$ROOT/../xiv-arcade-build/release}"
REPO="${REPO:-${GITHUB_REPOSITORY:-Spaceghost/xivarcade-dalamud}}"
MANIFEST="$ROOT/src/XivArcade.Plugin/XivArcade.json"

[[ -f "$BIN/XivArcade.dll" ]] || {
  echo "error: $BIN/XivArcade.dll missing; run: dotnet build XivArcade.slnx -c Release" >&2
  exit 1
}
[[ -f "$MANIFEST" ]] || { echo "error: $MANIFEST missing" >&2; exit 1; }

VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["AssemblyVersion"])' "$MANIFEST")"
[[ -n "$VERSION" ]] || { echo "error: no AssemblyVersion in $MANIFEST" >&2; exit 1; }
if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
  SOURCE_DATE_EPOCH="$(git -C "$ROOT" log -1 --format=%ct 2>/dev/null || echo 0)"
fi

STAGE="${TMPDIR:-/tmp}/xivarcade-package-$$"
trap 'rm -rf "$STAGE"' EXIT
rm -rf "$STAGE"
mkdir -p "$STAGE/XivArcade" "$OUT"

# Everything the built plugin needs and nothing else: the managed assemblies and the
# manifest. Dalamud provides its own assemblies (the SDK keeps those out of the output),
# .pdb and .deps.json are for developers, and DalamudPackager leaves its own zip and
# staging folder in the output.
( cd "$BIN" && find . -type f -print0 | while IFS= read -r -d '' f; do
    # A case pattern's * matches slashes too, so the narrow paths come first.
    case "$f" in
      ./runtimes/win-x64/native/*) ;;                                  # native code for the game's process, if any
      ./runtimes/* | ./XivArcade/*) continue ;;                          # other platforms; DalamudPackager's own staging copy
      ./*.deps.json | ./*.runtimeconfig.json | *.pdb | *.zip) continue ;;
      ./*/*) continue ;;                                               # nothing else from a subfolder
      ./*.dll | ./*.json) ;;
      *) continue ;;
    esac
    mkdir -p "$STAGE/XivArcade/$(dirname "$f")"
    cp "$f" "$STAGE/XivArcade/$f"
  done )
cp "$MANIFEST" "$STAGE/XivArcade/XivArcade.json"

# Dalamud unpacks the zip straight into the plugin folder, so the files sit at the root.
find "$STAGE/XivArcade" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
rm -f "$OUT/latest.zip" "$OUT/XivArcade-$VERSION.zip"
( cd "$STAGE/XivArcade" && find . -type f | LC_ALL=C sort | sed 's|^\./||' |
    TZ=UTC zip -X -D -q "$OUT/latest.zip" -@ )
cp "$OUT/latest.zip" "$OUT/XivArcade-$VERSION.zip"
echo "== $OUT/latest.zip"
# Listed once and kept: `unzip -l | grep -q` makes unzip die of SIGPIPE, which under
# `set -o pipefail` fails the check even when the file is there.
ZIP_ENTRIES="$(unzip -Z1 "$OUT/latest.zip")"
printf '%s\n' "$ZIP_ENTRIES"
# What Dalamud opens the zip for. A missing manifest installs a plugin that cannot load.
for want in XivArcade.dll XivArcade.json ; do
  printf '%s\n' "$ZIP_ENTRIES" | grep -qxF "$want" || { echo "error: $want is not in the zip" >&2; exit 1; }
done

# The listing: the shipped manifest plus the fields a plugin repository adds.
CHANNEL=stable
LISTING="$OUT/pluginmaster.json"
if [[ "${TESTING:-0}" == 1 ]]; then CHANNEL=testing; LISTING="$OUT/pluginmaster-testing.json"; fi
# With RELEASE_TAG (the release workflow sets it), the listing also carries what changed,
# from the changelog, for the installer to show.
NOTES=()
if [[ -n "${RELEASE_TAG:-}" ]]; then
  NOTES=(--changelog "$(python3 "$ROOT/tools/releasekit.py" installer-notes "$RELEASE_TAG")")
fi
python3 "$ROOT/tools/pluginmaster.py" \
  --manifest "$MANIFEST" --repo "$REPO" --channel "$CHANNEL" \
  --last-update "$SOURCE_DATE_EPOCH" "${NOTES[@]}" --out "$LISTING"
echo "== $LISTING"
cat "$LISTING"
