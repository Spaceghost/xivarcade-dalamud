# Arcade: your own classic games as game panels, saves kept in step

`/arcade` lists the classic games **you** put in a folder, starts them in RetroArch on your Linux
desktop so they appear as a panel in the game (pin it, pet it, full-screen it like any other panel),
and keeps every battery save and save state in one folder that Syncthing copies between your machines.

**Status: beta.** The host helper and the plugin's logic are covered by tests. None of it has been seen
working in the game, with a real emulator, or syncing between two real machines. See
[What is verified](#what-is-verified-and-what-is-not).

## Bring your own games

XivArcade never downloads, links to or helps find ROMs, disc images or BIOS files. It only reads
folders you point it at. Use your own dumps of games you own. The same goes for artwork: a picture is
shown only when the file is already on your disk; nothing is scraped. PlayStation and PlayStation 2
cores want a BIOS dumped from your own console; XivArcade will not look for one.

## What you type

| Command | What it does |
| --- | --- |
| `/arcade` | Opens the Arcade window. With no games it is the three-step welcome. |
| `/arcade <name>` | Plays the closest match: `/arcade ff7`, `/arcade tactics`, `/arcade x-2`. |
| `/arcade last` | Resumes the most recent game. |
| `/arcade list` | Prints the library by console. |
| `/arcade sync` | Forces a save sync and prints its status. |
| `/arcade setup` | The first-run checklist, in the window and in chat. |
| `/arcade rescan` | Looks at the folders again (the window does this by itself while open). |
| `/arcade launchbox <folder>` | Reads a LaunchBox folder where it lives. |
| `/arcade import-saves <folder>` | Copies saves from another RetroArch install. Never over a newer save. |
| `/arcade launch --dry-run <name or file>` | Prints the exact emulator command and starts nothing. |
| `/arcade pad` · `pad on` · `pad off` · `pad auto` | Who has the gamepad. `auto` (default): the arcade game while its panel has the focus. `on`: arcade mode by hand for the running game. `off`: FFXIV keeps it. See [Controller](#controller). |
| `/arcade emulator` · `emulator <console or game> <choice>` | Lists the emulators per console; sets the preferred one (`auto`, `retroarch:<core>`, `pcsx2`, `duckstation`). |
| `/arcade bios` | Which BIOS files your emulators read, their sizes and MD5s, the folder, and what is there. |
| `/arcade paths` · `games-folder <dir>` · `saves-folder <dir>` | The folders in use; your own games folder; your own synced saves folder (existing saves are not moved). |

If XivDesktop is installed, its launcher palette (Super+D) finds your games by name, with an `Arcade` badge.
In the window: type to search, arrows move, Enter plays, F5 rescans, F6 syncs saves, Esc closes.

## First run: three steps

Opening `/arcade` creates everything that can be created for you and shows three checks that turn
green by themselves:

1. **Choose your games folder.** The default, `~/Games/Arcade/`, is made for you with one folder per
   console and a `README.txt` in each naming the accepted file types. Another place:
   `xiv-arcade setup --games /some/where`.
2. **Install the emulator.** One line, shown with a Copy button. It installs RetroArch from Flathub and
   lets its sandbox reach the games folder and the saves folder. Nothing is installed for you.
   Cores are RetroArch's own download: *Main Menu > Online Updater > Core Downloader*. The window
   names the exact core for each console you have games for.
3. **Drop your own game files in.** Copy them into the console folders. They appear in the window
   without a restart.

Below the steps is the Final Fantasy shelf: titles, consoles and years as plain text. An entry
lights up when a matching file of yours appears. Anything else lands under "Everything else", by console.

### Folders and consoles

| Folder | Console | Files | Core (first found wins) |
| --- | --- | --- | --- |
| `nes` | NES / Famicom | `.nes .fds .unf` | mesen, nestopia, fceumm |
| `snes` | Super NES / Super Famicom | `.sfc .smc` | snes9x, bsnes |
| `gb`, `gbc` | Game Boy, Game Boy Color | `.gb`, `.gbc` | gambatte, sameboy, mgba |
| `gba` | Game Boy Advance | `.gba` | mgba, vba_next |
| `nds` | Nintendo DS | `.nds` | melondsds, melonds, desmume |
| `wonderswan` | WonderSwan | `.ws .wsc` | mednafen_wswan |
| `psx` | PlayStation | `.cue .chd .pbp .m3u .iso` | swanstation, mednafen_psx_hw, mednafen_psx, pcsx_rearmed |
| `ps2` | PlayStation 2 | `.iso .chd .cso` | pcsx2 (LRPS2). Standalone PCSX2 is not wired up. |
| `psp` | PlayStation Portable | `.iso .cso .pbp` | ppsspp |
| `genesis`, `n64` | Mega Drive, Nintendo 64 | `.md .gen`, `.z64 .n64` | genesis_plus_gx, mupen64plus_next |

The console comes from the folder name, then from an extension only one console uses. An `.iso` or
`.chd` outside a console folder is ambiguous and is left out until you put it in one.

Multi-disc games named `Game (Disc 1).chd`, `Game (Disc 2).chd` become one entry. The playlist
(`.m3u`) is written to `~/.config/xiv-arcade/playlists/`, never into your games folder. Your own `.m3u`
is used as it is. On the Super NES, "Final Fantasy II" and "III" light up IV and VI.

Artwork is optional: an image with the game's name (`.png`, `.jpg`) beside it or in a `boxart/` folder.

## Coming from LaunchBox

Copy your LaunchBox folder, or only its `Games`, `Images` and `Data` folders, into `~/Games/Arcade/`
as they are. Or leave it where it is and point at it: `/arcade launchbox /path/to/LaunchBox` (the
welcome screen has the same one-step box). What is read:

- **`Games/<Platform Name>/`** with LaunchBox's platform names, in any case: "Nintendo Entertainment
  System", "Super Nintendo Entertainment System", "Nintendo Game Boy", "Nintendo Game Boy Color",
  "Nintendo Game Boy Advance", "Nintendo DS", "Sony Playstation", "Sony Playstation 2", "Sony PSP",
  "Sega Genesis", "Nintendo 64", "WonderSwan Color".
- **`Data/Platforms/<Platform>.xml`** when present: titles, sort titles, release years, and each
  game's `ApplicationPath`. Windows paths are normalised: backslashes, drive letters and absolute
  paths such as `D:\LaunchBox\Games\...` are matched to the longest tail that exists under the copied
  folder, ignoring case. Discs listed as `AdditionalApplication` entries with a `Disc` number become
  one `.m3u`. A game whose file was not copied is simply absent.
- **`Images/<Platform>/Box - Front/`**, then `Box - 3D`, `Clear Logo`, `Screenshot - Game Title`,
  `Screenshot - Gameplay`, including region subfolders. LaunchBox's filename sanitising (`:` and `'`
  become `_`) and its `-01`, `-02` suffixes are tolerated; the lowest number wins.

A game is its console plus its title, so the same game in the LaunchBox tree and in your plain
`<console>/` folders is one entry. `Emulators/`, `Metadata/` and the like are skipped.

Saves from a Windows RetroArch (for example `LaunchBox\Emulators\RetroArch`):
`/arcade import-saves /path/to/RetroArch`. It reads `saves/` and `states/` with their per-core
subfolders. A save named after a game in your library goes to that game's console; otherwise the core
folder decides (Snes9x, mGBA, SwanStation, PPSSPP...). A save that is already here and newer, or the
same age, is left alone. Files it cannot place are listed, not guessed.

## How launching works

The plugin hands one shell line to ghostty-dalamud over IPC (`GhosttyDalamud.v1.Call`, or the older Post
gate), which starts it inside the host agent's headless compositor; the emulator's window is then a game
panel. Without ghostty-dalamud the window says so; housekeeping (setup, scan, sync) runs through Wine's
`start /unix`, and a game starts that way only if you tick *Play on the Linux desktop instead*, in which
case it opens outside FFXIV. Either way the line runs the helper, and the helper:

1. asks Syncthing to rescan and waits up to 30 s for incoming saves; **if saves are still arriving it
   refuses to start the game** and says so,
2. resolves any conflict (below) and copies the game's current saves to
   `~/.local/share/xiv-arcade/backups/<game>/<time>/` (last 10 kept, not synced),
3. runs `flatpak run org.libretro.RetroArch -L <core>.so --appendconfig <console>.cfg <your file>`,
4. when the emulator closes, records the play, asks for another rescan so the new save goes out, and
   writes the result where the window reads it.

Your own `retroarch.cfg` is never edited. The appended config points `savefile_directory` and
`savestate_directory` at the synced tree, turns off RetroArch's per-core sorting so the path is the
same on every machine whatever core runs the game, and saves battery RAM every 10 s.

### Controller

Not verified in game. The agent's compositor forwards keyboard and pointer to a focused panel. A gamepad
does not go through it: the emulator reads the pad from the Linux host directly, and FFXIV reads the same
pad. So XivArcade **hides the pad from FFXIV while you are playing**, on the game side:

- **How.** A hook on the game's own gamepad poll (`PadDevice::Update`, called `Poll` in older FFXIVClientStructs, through Dalamud's hooking service and
  FFXIVClientStructs). It is the function Dalamud itself hooks to keep the pad from the game while its
  gamepad navigation is on; Dalamud gives plugins only the read-only `IGamepadState`, no switch, so the
  plugin has its own hook. The game polls the pad as always; then, only while capture is on, sticks and
  buttons are zeroed so FFXIV sees an idle controller. It can only blank: it never presses, moves or
  sends anything, to the game or to the emulator.
- **When.** While an arcade game is running (the helper is alive: `/proc/<pid>`) **and** its panel has
  the focus, asked of ghostty-dalamud five times a second (`focus.get`; the panel is the one your
  `window.open` request became). Without panels (a game on the Linux desktop) `/arcade pad on` turns
  "arcade mode" on by hand for the running game.
- **You always see it.** A pulsing banner across the top of the screen for as long as the pad is
  captured, and a chat line each time it changes hands.
- **Getting it back.** Hold **Start+Select for one second** (read before anything is hidden, so it works
  while captured; the held buttons then stay hidden until you let go, two seconds at most, so Start does
  not open FFXIV's menu), press **Esc**, or type `/arcade pad off`. It is also released by itself when
  the emulator exits, the panel loses focus, you enter combat, a cutscene starts, you change zone, you
  log out, and when the plugin unloads (the hook is removed first). After a forced release it stays
  released until you focus the panel again or type `/arcade pad on`; it never re-arms by itself.
- **Fail-safe.** If the hook cannot be installed (a game patch Dalamud has not caught up with), FFXIV is
  left exactly as it was, and the first-run list in the window says so. The hook hides the pad only if
  the plugin's frame tick confirmed it within the last half second, so a stalled or crashed tick gives
  the pad back; a fault inside the hook turns it off until the plugin reloads.
- `/arcade pad` shows the state; `/arcade pad off` leaves the pad with FFXIV for good (the old
  behaviour); `/arcade pad auto` is the default.

What is not known until it is tried in the game: whether zeroing the digital state is enough for every
FFXIV input path (Dalamud's own note says it blocks all input; the analog trigger values are left alone),
whether Esc reaches Dalamud's key state while a ghostty panel has the keyboard (Start+Select does not
depend on that), and whether `pause_nonactive` makes RetroArch itself ignore the pad when its panel is
not focused: if it does not, the emulator still reacts to the pad while you play FFXIV with it.

### Emulators: RetroArch cores and standalone emulators

Each console runs in the emulator you prefer, else the best one installed: `/arcade emulator` lists the
choices, `/arcade emulator ps2 pcsx2` sets one for a console, `/arcade emulator final fantasy x
retroarch:pcsx2` for one game, `auto` clears it (`xiv-arcade emulator ...` on the host is the same).
A preference that is not installed falls back to `auto`.

| Choice | What runs | Saves |
| --- | --- | --- |
| `retroarch:<core>` | `flatpak run --filesystem=... org.libretro.RetroArch -L <core>.so --appendconfig <console>.cfg FILE` (or native `retroarch`) | `saves/<console>/`, `states/<console>/` |
| `pcsx2` (PlayStation 2) | Flatpak `net.pcsx2.PCSX2` or native `pcsx2-qt`: `-batch -nogui -fullscreen -- FILE` (a multi-disc game passes its first disc) | `saves/ps2/PCSX2/` (memory cards), `states/ps2/PCSX2/` |
| `duckstation` (PlayStation) | Flatpak `org.duckstation.DuckStation` or native `duckstation-qt`: `-batch -fullscreen -- FILE` | `saves/psx/DuckStation/`, `states/psx/DuckStation/` |

**Your own emulator settings are never edited.** A standalone emulator is started against a generated
profile in `~/.config/xiv-arcade/emulators/<name>/`: a copy of your own settings file (pad bindings,
graphics) in which only the memory-card and save-state folders point into the synced tree and relative
folders (BIOS) are made absolute so they still mean your folders. The emulator is sent there by setting
its XDG directory for that one process (`XDG_CONFIG_HOME` for PCSX2, `XDG_DATA_HOME` for DuckStation;
`flatpak run --env=` under Flatpak, with `--filesystem=` for exactly the profile, the saves and the
game's folder). The copy is refreshed at every launch, so a change you make inside the game session is
not carried back: change settings in your own emulator. A standalone's memory cards are shared by its
games, so the whole folder is backed up before each launch. Not run against a real PCSX2 or DuckStation:
the flags and ini keys are from their documentation. PPSSPP and melonDS standalones are not wired up
(their save locations do not follow this pattern); their RetroArch cores are.

### BIOS files

XivArcade never downloads, links to or helps you find a BIOS. It removes the guesswork about your own
dump: `/arcade bios` (and a step in the first-run list for a console you have games for) names the
exact file the chosen emulator reads, its size and MD5, and the folder to put it in (RetroArch's
`system_directory` as your `retroarch.cfg` has it, `system/pcsx2/bios/` for LRPS2, your own BIOS folder
for a standalone). The folder is watched while the window is open: the step turns green when a correct
file appears. A file with the right name and the wrong content is told apart: wrong size, another
region's dump under this name ("rename it to ..."), a different revision or bad dump (MD5 differs), or
upper-case letters in the name.

| Console | Files | Needed? |
| --- | --- | --- |
| PlayStation | `scph5500.bin` (Japan), `scph5501.bin` (North America), `scph5502.bin` (Europe): 524,288 bytes each | By the accurate cores (SwanStation, Beetle PSX) and DuckStation; Beetle wants the game's region. **Not by PCSX ReARMed**: with no BIOS present and that core installed, `auto` picks it and says "no BIOS needed, lower compatibility", so Final Fantasy VII, VIII and IX can start without one. A BIOS-sized file of a revision this list does not know keeps you on the accurate core. |
| PlayStation 2 | any PS2 BIOS dump, usually 4,194,304 bytes | **Always.** No core or emulator has a BIOS-free mode. Every console revision has its own checksum, so the file is checked by content (its ROMDIR table and ROMVER) and its version and region are shown. |
| Game Boy Advance, Nintendo DS | `gba_bios.bin`; `bios7.bin`, `bios9.bin` | Optional: the cores have built-in replacements. |

A missing or unrecognised BIOS never blocks a launch (the list of known dumps is short on purpose); the
helper prints a note and the emulator has the last word.

## Save sync

Every save and state lives in one tree:

```
~/.local/share/xiv-arcade/sync/
  saves/<console>/     battery saves, memory cards
  states/<console>/    save states
  .stignore            temp and editor files never travel
```

Syncthing copies that folder between your machines, peer to peer; over Tailscale it needs no hub and
no open ports. The generated folder config turns **file versioning on** (staggered, one year) and sets
`maxConflicts` to unlimited, so a replaced or conflicting save is never destroyed.

XivArcade does not install or start Syncthing, enable a service, or pair a device. It prints the
commands. The minimum, on each machine:

```sh
# 1. Syncthing itself (pick what fits the machine), running as your user
brew install syncthing && brew services start syncthing     # or your distribution's package, then:
systemctl --user enable --now syncthing.service

# 2. The Arcade saves folder, with versioning on (prints the commands; --apply runs them)
python3 ~/.config/xiv-arcade/xiv-arcade sync-setup --apply

# 3. Pair: print this machine's ID, then on the OTHER machine add it
python3 ~/.config/xiv-arcade/xiv-arcade pair
python3 ~/.config/xiv-arcade/xiv-arcade pair DEVICE-ID --name other-pc --apply
#    to keep traffic on your tailnet add:  --address tcp://<tailscale-name>:22000
```

Do step 3 in both directions, or accept the prompt in Syncthing's web page on the second machine.
`sync-setup` also prints the `<folder>` XML to paste into `config.xml` if you prefer that. The helper is
the plugin's own copy; `tools/xiv-arcade` in the repository is the same file and runs without the game.
The exact `syncthing cli` spellings have not been run against a real Syncthing; if one is refused,
nothing further runs, and the XML or the web page does the same job.

### What the status means

| Status | Meaning | Can you launch? |
| --- | --- | --- |
| In sync / Ready · nothing to sync yet | Up to date with every connected device. | yes |
| Ready · no other device yet | Folder set up, nothing paired. | yes |
| Sending saves… | This machine has the newest saves and is pushing them. | yes |
| **Syncing…** | Newer saves are still arriving. | **no**, until they are complete |
| Other device offline | Saves go out when it returns. If it also played, both saves are kept. | yes |
| Conflict: both saves kept | See below. | yes |
| Sync is off / not set up / paused / error | Saves stay on this machine. | yes |

### Conflicts

If the same save changed on two machines, Syncthing keeps the losing copy as
`name.sync-conflict-DATE-TIME-DEVICE.ext`. The helper then makes the **newer** file (by modification
time) the live save and keeps the other beside it as
`name (older save, device ABCDEFG, 2026-09-20 14.03).srm`. Nothing is deleted. The window and chat tell
you, and the conflict is recorded. To go back, swap the two files' names. Older versions are also in
Syncthing's `.stversions/` and in the pre-launch backups.

## Where things live

| Path | What |
| --- | --- |
| `~/Games/Arcade/` | Your games (default). Not synced. `/arcade games-folder DIR` or `xiv-arcade setup --games DIR`. |
| `$XDG_DATA_HOME/xiv-arcade/sync/` | Saves and states. The only synced tree. `/arcade saves-folder DIR` or `xiv-arcade setup --saves DIR`. |
| `$XDG_DATA_HOME/xiv-arcade/backups/` | Pre-launch save copies. Local. |
| `$XDG_CONFIG_HOME/xiv-arcade/arcade.db` | SQLite: library index, play history, sync state, conflicts. |
| `$XDG_CONFIG_HOME/xiv-arcade/state.json` | What the window reads. Rewritten by every helper command. |
| `$XDG_CONFIG_HOME/xiv-arcade/config.json` | Games and saves folders, extra folders, emulator preferences. Your device names stay here, never in a repository. |
| `$XDG_CONFIG_HOME/xiv-arcade/emulators/` | Generated profiles for standalone emulators. |
| `$XDG_CONFIG_HOME/xiv-arcade/xiv-arcade` | The helper, written by the plugin from its own build. |

`/arcade paths` (or `xiv-arcade paths`) prints what is in use. The rules:

- `XDG_CONFIG_HOME`, `XDG_DATA_HOME` and `XDG_CACHE_HOME` are honoured by the helper and the plugin
  (`~/.config`, `~/.local/share`, `~/.cache` when unset; a relative value is ignored, as the
  specification says). The plugin's copy of the helper uses the directory it was written to, so the two
  cannot disagree.
- **An existing setup is never moved.** If `~/.config/xiv-arcade/config.json` or
  `~/.local/share/xiv-arcade/sync/` exists and the XDG place has nothing yet, the existing one keeps
  being used and a note says so. `setup --saves DIR` changes where saves go and leaves the old saves
  where they are; add `--copy-saves` to copy them (never over a newer file, originals kept), and point the
  Syncthing folder at the new place.
- Flatpak RetroArch keeps its config under `~/.var/app/org.libretro.RetroArch/config/retroarch`
  whatever `XDG_CONFIG_HOME` is; native RetroArch under `$XDG_CONFIG_HOME/retroarch`. Its
  `libretro_directory` and `system_directory` are read from your `retroarch.cfg` (never written).
  Flatpak apps are looked for system-wide and under `$XDG_DATA_HOME/flatpak`.
- **Wine to Linux paths are found, not assumed.** The plugin runs under Wine and opens Linux files
  through whatever the prefix calls the Linux root: the first drive from `Z:` down that really holds
  `/proc` and `/etc`, else Wine's `\\?\unix` namespace, else `Z:` as a guess that `/arcade paths`
  labels as one. `WineRootOverride` in the plugin's config forces it. The home directory comes from
  `$HOME`, `WINEHOMEDIR` (both the `Z:` and the `unix` forms) or the plugin's own config path.

## What is verified and what is not

Verified by tests (`python3 -B -m unittest discover -s tests/arcade`, and the C# suite), using empty
placeholder files only: console detection from paths, LaunchBox platform names, XML parsing, Windows
path normalisation, image lookup, multi-disc playlists, fuzzy matching, the emulator command per core
(also via `--dry-run`), the sync state machine (newer wins, conflict keeps both, offline peer, refusal
while saves arrive), save import never overwriting a newer save, the SQLite index, the first-run state
with no games, shell quoting of names and paths, and the palette rows.

Also by tests: the gamepad decision with a fake pad, focus source and hook (capture only while a game
runs and its panel has the focus, every release condition, the Start+Select second, the stale-tick
fail-safe, a hook that cannot be installed, the hook removed exactly once on unload and no allocation
per tick), emulator choice and preference, the PCSX2 and DuckStation command lines and generated
profiles (the player's ini untouched), BIOS matching by size and MD5 with placeholder files and a
hand-made PS2 ROMDIR, XDG and custom paths, keeping an existing setup in place, and the Wine path
mapping.

**Run against the real programs:** the `syncthing cli` commands that `sync-setup` and `pair` print, on
Syncthing 1.30.0 and 2.0.10 with a throwaway home, checking the resulting `config.xml` (folder,
staggered versioning with `maxAge`, `maxConflicts` -1, `ignorePerms`, the device and its share). Two
things differed and are fixed: 1.30 accepts `--addresses` on `devices add` but leaves the address
`dynamic`, so the address is also set with `devices ID addresses 0 set`; and the device ID is
`syncthing device-id` on 2.x but `syncthing --device-id` on 1.x, so both are tried.

**Not verified:** anything in the game (the window, the panel, focus, the gamepad hook and its banner),
any real emulator or core (RetroArch, PCSX2, DuckStation), the Wine `start /unix` fallback, the derived
Wine root on a real prefix, and sync between two real machines. **RetroArch honouring `--appendconfig`
inside Flatpak was not checked**: the build container has no Flatpak, and nothing was installed
system-wide to get one. What changed instead is that the launch no longer depends on a one-time
`flatpak override`: every run passes `--filesystem=` for the appended config, the saves and the game's
folder. No game was launched: no test ROM was used, so launching is covered
by command construction and `--dry-run` only.
