"""Host tests for the parts of tools/xiv-arcade that replaced the first release's known limits: XDG and custom
paths, standalone emulators, the emulator preference, and BIOS matching. Every "BIOS" here is a placeholder the
test writes (zeros, or a few hand-made bytes); no real BIOS, game or emulator is used or fetched."""
import hashlib
import json
from pathlib import Path
from unittest import mock

from test_xiv_arcade import FakeSync, Tree, xa

ZEROS = bytes(524288)
PLACEHOLDER = (xa.BiosFile("scph5500.bin", 524288, hashlib.md5(ZEROS).hexdigest(), "placeholder, Japan"),
               xa.BiosFile("scph5501.bin", 524288, hashlib.md5(b"\1" + ZEROS[1:]).hexdigest(), "placeholder, North America"))


def fake_ps2_bios(romver=b"0160EC20020426"):
    """A ROMDIR table with RESET, ROMDIR and ROMVER entries, and the ROMVER text where the table says it is."""
    def entry(name, size):
        return name.ljust(10, b"\0") + b"\0\0" + size.to_bytes(4, "little")
    head = bytearray(0x2000)
    table = entry(b"RESET", 0x2000) + entry(b"ROMDIR", 0x40) + entry(b"ROMVER", 16) + bytes(16)
    body = head + table.ljust(0x40, b"\0") + romver.ljust(16, b"\0")
    return bytes(body).ljust(1 << 20, b"\0")


class Paths(Tree):
    def test_xdg_variables_are_honoured_and_relative_ones_ignored(self):
        env = xa.Env(self.home, environ={"XDG_CONFIG_HOME": "/x/cfg", "XDG_DATA_HOME": "/x/data", "XDG_CACHE_HOME": "relative/cache"})
        self.assertEqual(Path("/x/cfg/xiv-arcade"), env.config)
        self.assertEqual(Path("/x/data/xiv-arcade/sync"), env.sync_root)
        self.assertEqual(self.home / ".cache/xiv-arcade", env.cache)
        self.assertIn(Path("/x/data/flatpak/app"), env.flatpak_roots)

    def test_the_plugins_copy_uses_its_own_directory(self):
        script = self.home / "elsewhere/xiv-arcade/xiv-arcade"
        env = xa.Env.detect(self.home, environ={"XDG_CONFIG_HOME": "/x/cfg"}, script=script)
        self.assertEqual(script.parent, env.config)
        self.assertEqual(Path("/x/cfg/xiv-arcade"), xa.Env.detect(self.home, environ={"XDG_CONFIG_HOME": "/x/cfg"},
                                                                   script=self.home / "src/tools/xiv-arcade").config)

    def test_an_existing_default_setup_is_kept_where_it_is(self):
        self.touch(self.home / ".config/xiv-arcade/config.json", "{}")
        save = self.touch(self.home / ".local/share/xiv-arcade/sync/saves/snes/Game.srm", "save")
        env = xa.Env.detect(self.home, environ={"XDG_CONFIG_HOME": str(self.home / "xdg/c"), "XDG_DATA_HOME": str(self.home / "xdg/d")})
        self.assertEqual(self.home / ".config/xiv-arcade", env.config)
        self.assertEqual(self.home / ".local/share/xiv-arcade/sync", env.sync_root)
        self.assertEqual(2, len(env.notes))
        self.assertTrue(save.is_file())
        fresh = xa.Env.detect(self.home / "nobody", environ={"XDG_DATA_HOME": str(self.home / "xdg/d")})
        self.assertEqual(self.home / "xdg/d/xiv-arcade", fresh.data)

    def test_custom_saves_root_moves_nothing_unless_asked(self):
        xa.setup(self.env)
        old = self.touch(self.env.sync_root / "saves/snes/Game.srm", "old save")
        new = self.home / "Sync/arcade"
        xa.setup(self.env, saves=str(new))
        self.assertEqual(new, self.env.sync_root)
        self.assertTrue(old.is_file())
        self.assertFalse((new / "saves/snes/Game.srm").exists())
        self.assertIn("were not moved", self.env.notes[-1])
        self.assertIn(f'savefile_directory = "{new}/saves/snes"', (self.env.config / "cfg/snes.cfg").read_text())
        self.assertIn(str(new), xa.syncthing_folder_xml(self.env))
        self.assertIn(f"--filesystem={new}", xa.install_line(self.env))

    def test_copy_saves_copies_and_keeps_the_originals(self):
        xa.setup(self.env)
        old = self.touch(self.env.sync_root / "saves/snes/Game.srm", "old save")
        new = self.home / "Sync/arcade"
        self.touch(new / "saves/snes/Newer.srm", "x")
        xa.setup(self.env, saves=str(new), copy_saves=True)
        self.assertEqual("old save", (new / "saves/snes/Game.srm").read_text())
        self.assertTrue(old.is_file())

    def test_custom_games_root_and_state_paths(self):
        xa.setup(self.env, games=str(self.home / "roms"))
        index = xa.Index(self.env.db_path)
        state = xa.build_state(self.env, index, FakeSync())
        index.close()
        self.assertEqual(str(self.home / "roms"), state["paths"]["games"])
        self.assertTrue((self.home / "roms/psx").is_dir())

    def test_native_retroarch_follows_xdg_config_home_and_its_own_cfg(self):
        env = xa.Env(self.home, config=self.env.config, data=self.env.data, which=lambda n: "/usr/bin/" + n if n == "retroarch" else None,
                     flatpak_roots=(self.home / "none",), environ={"XDG_CONFIG_HOME": str(self.home / "xdg")})
        self.touch(self.home / "xdg/retroarch/retroarch.cfg", 'libretro_directory = "~/cores"\nsystem_directory = ":/bios"\n')
        self.touch(self.home / "cores/snes9x_libretro.so")
        front = xa.find_frontend(env)
        self.assertEqual(("native", self.home / "xdg/retroarch"), (front.kind, front.config_dir))
        self.assertEqual(self.home / "cores/snes9x_libretro.so", xa.find_core(front, xa.SYSTEM_BY_ID["snes"]))
        self.assertEqual(self.home / "xdg/retroarch/bios", xa.bios_dir(env, front, xa.SYSTEM_BY_ID["psx"], None))

    def test_flatpak_retroarch_ignores_the_outer_xdg_config_home(self):
        env = xa.Env(self.home, config=self.env.config, data=self.env.data, which=lambda n: None,
                     flatpak_roots=(self.home / "flatpak/app",), environ={"XDG_CONFIG_HOME": "/x/cfg"})
        self.install_retroarch("snes9x")
        self.assertEqual(self.home / ".var/app/org.libretro.RetroArch/config/retroarch", xa.find_frontend(env).config_dir)


class SyncthingSpellings(Tree):
    """The spellings below were run against real Syncthing 1.30.0 and 2.0.10 binaries (see docs/ARCADE.md)."""

    def test_pairing_sets_the_address_in_a_way_both_major_versions_apply(self):
        cmds = xa.pair_commands("ABCDEFG-1", "other pc", "tcp://peer:22000")
        self.assertEqual("syncthing cli config devices add --device-id ABCDEFG-1 --name 'other pc' --addresses tcp://peer:22000", cmds[0])
        self.assertEqual("syncthing cli config devices ABCDEFG-1 addresses 0 set tcp://peer:22000", cmds[1])
        self.assertEqual("syncthing cli config folders xiv-arcade-saves devices add --device-id ABCDEFG-1", cmds[2])
        self.assertEqual(2, len(xa.pair_commands("ABCDEFG-1", "x", None)))

    def test_device_id_tries_the_v2_subcommand_then_the_v1_flag(self):
        self.assertEqual([["syncthing", "device-id"], ["syncthing", "--device-id"]], xa.device_id_commands())


class Emulators(Tree):
    GAME = {"id": "ps2-ffx", "title": "Final Fantasy X", "system": "ps2", "path": "/g/ps2/Final Fantasy X.iso", "discs": []}

    def install_flatpak(self, app):
        (self.home / "flatpak/app" / app).mkdir(parents=True)

    def prefer(self, **table):
        xa.atomic_write(self.env.settings_path, json.dumps(table))

    def test_auto_prefers_the_core_then_the_standalone(self):
        xa.setup(self.env)
        self.install_flatpak("net.pcsx2.PCSX2")
        argv, choice = xa.plan_launch(self.env, self.GAME)
        self.assertEqual(("pcsx2", "flatpak"), (choice.id, choice.kind))
        root = self.env.config / "emulators/pcsx2"
        self.assertEqual(["flatpak", "run", f"--env=XDG_CONFIG_HOME={root}", f"--filesystem={root}",
                          f"--filesystem={self.env.sync_root}", "--filesystem=/g/ps2", "net.pcsx2.PCSX2",
                          "-batch", "-nogui", "-fullscreen", "--", "/g/ps2/Final Fantasy X.iso"], argv)
        self.install_retroarch("pcsx2")
        self.assertEqual("retroarch", xa.plan_launch(self.env, self.GAME)[1].kind)

    def test_preference_per_console_and_per_game(self):
        xa.setup(self.env)
        self.install_retroarch("pcsx2")
        self.install_flatpak("net.pcsx2.PCSX2")
        self.prefer(emulators={"ps2": "pcsx2"})
        self.assertEqual("pcsx2", xa.plan_launch(self.env, self.GAME)[1].id)
        self.assertIsNotNone(xa.plan_launch(self.env, self.GAME)[1].standalone)
        self.prefer(emulators={"ps2": "pcsx2"}, game_emulators={"ps2-ffx": "retroarch"})
        self.assertEqual("retroarch", xa.plan_launch(self.env, self.GAME)[1].kind)
        self.assertIsNotNone(xa.plan_launch(self.env, {**self.GAME, "id": "ps2-other"})[1].standalone)

    def test_a_preferred_emulator_that_is_not_installed_falls_back(self):
        xa.setup(self.env)
        self.install_retroarch("pcsx2")
        self.prefer(emulators={"ps2": "pcsx2"})
        self.assertEqual("retroarch", xa.plan_launch(self.env, self.GAME)[1].kind)

    def test_set_preference_validates_and_auto_clears(self):
        xa.setup(self.env)
        self.assertIn("pcsx2", xa.set_preference(self.env, "ps2", "PCSX2", []))
        self.assertEqual({"ps2": "pcsx2"}, self.env.settings()["emulators"])
        xa.set_preference(self.env, "Final Fantasy X", "retroarch:pcsx2", [self.GAME])
        self.assertEqual({"ps2-ffx": "retroarch:pcsx2"}, self.env.settings()["game_emulators"])
        xa.set_preference(self.env, "ps2", "auto", [])
        self.assertEqual({}, self.env.settings()["emulators"])
        for target, value in (("ps2", "duckstation"), ("nowhere", "auto"), ("snes", "pcsx2")):
            with self.assertRaises(xa.Refused):
                xa.set_preference(self.env, target, value, [])

    def test_native_pcsx2_and_the_first_disc_of_a_playlist(self):
        env = xa.Env(self.home, config=self.env.config, data=self.env.data, which=lambda n: "/usr/bin/" + n if n == "pcsx2-qt" else None,
                     flatpak_roots=(self.home / "none",), environ={})
        game = {**self.GAME, "path": "/g/ps2/Game.m3u", "discs": ["/g/ps2/Game (Disc 1).iso", "/g/ps2/Game (Disc 2).iso"]}
        argv, choice = xa.plan_launch(env, game)
        self.assertEqual(["env", f"XDG_CONFIG_HOME={env.config / 'emulators/pcsx2'}", "pcsx2-qt", "-batch", "-nogui", "-fullscreen",
                          "--", "/g/ps2/Game (Disc 1).iso"], argv)

    def test_generated_profile_redirects_saves_and_leaves_the_players_ini_alone(self):
        mine = self.home / ".var/app/net.pcsx2.PCSX2/config/PCSX2"
        text = "[UI]\nTheme = dark\n[Folders]\nBios = bios\nMemoryCards = memcards\nCovers = /abs/covers\n[Pad1]\nType = DualShock2\n"
        ini = self.touch(mine / "inis/PCSX2.ini", text)
        self.install_flatpak("net.pcsx2.PCSX2")
        app = xa.STANDALONE_BY_ID["pcsx2"]
        made = xa.prepare_standalone(self.env, app, "flatpak").read_text()
        self.assertEqual(text, ini.read_text())
        self.assertEqual(self.env.config / "emulators/pcsx2/PCSX2/inis/PCSX2.ini", xa.standalone_root(self.env, app) / "PCSX2/inis/PCSX2.ini")
        self.assertIn(f"MemoryCards = {self.env.sync_root}/saves/ps2/PCSX2", made)
        self.assertIn(f"Savestates = {self.env.sync_root}/states/ps2/PCSX2", made)
        self.assertIn(f"Bios = {mine}/bios", made)
        self.assertIn("Covers = /abs/covers", made)
        self.assertIn("Theme = dark", made)
        self.assertIn("Type = DualShock2", made)
        self.assertTrue((self.env.sync_root / "saves/ps2/PCSX2").is_dir())
        self.assertLess(made.index("Savestates"), made.index("[Pad1]"))

    def test_a_profile_is_generated_without_a_player_ini(self):
        made = xa.standalone_ini_text(self.env, xa.STANDALONE_BY_ID["duckstation"], "duckstation-qt")
        self.assertIn(f"[MemoryCards]\nDirectory = {self.env.sync_root}/saves/psx/DuckStation", made)
        self.assertIn(f"SearchDirectory = {self.home}/.local/share/duckstation/bios", made)

    def test_memory_cards_of_a_standalone_are_backed_up(self):
        xa.setup(self.env)
        dirs = xa.standalone_dirs(self.env, xa.STANDALONE_BY_ID["pcsx2"])
        self.touch(dirs["saves"] / "Mcd001.ps2", "card")
        dest = xa.backup_saves(self.env, self.GAME, extra=dirs.values())
        self.assertEqual("card", (dest / "Mcd001.ps2").read_text())

    def test_state_lists_the_options(self):
        xa.setup(self.env)
        self.install_flatpak("net.pcsx2.PCSX2")
        index = xa.Index(self.env.db_path)
        ps2 = next(s for s in xa.build_state(self.env, index, FakeSync())["systems"] if s["id"] == "ps2")
        index.close()
        self.assertEqual("PCSX2 (standalone)", ps2["emulator"])
        self.assertEqual([("retroarch:pcsx2", False), ("pcsx2", True)], [(o["id"], o["installed"]) for o in ps2["emulators"]])


class Bios(Tree):
    def setUp(self):
        super().setUp()
        patch = mock.patch.dict(xa.BIOS_FILES, {"psx": PLACEHOLDER})
        patch.start()
        self.addCleanup(patch.stop)
        self.system_dir = self.home / ".var/app/org.libretro.RetroArch/config/retroarch/system"
        self.psx = xa.SYSTEM_BY_ID["psx"]

    def report(self, system=None):
        front = xa.find_frontend(self.env)
        system = system or self.psx
        return xa.bios_report(self.env, front, system, xa.choose_emulator(self.env, front, system))

    def test_missing_names_the_files_sizes_hashes_and_folder(self):
        self.install_retroarch("swanstation")
        r = self.report()
        self.assertFalse(r["ok"])
        self.assertEqual(str(self.system_dir), r["dir"])
        self.assertEqual(["missing", "missing"], [f["state"] for f in r["files"]])
        for want in PLACEHOLDER:
            self.assertIn(f"{want.name} (524,288 bytes, MD5 {want.md5}", r["summary"])
        self.assertIn("pcsx_rearmed", r["summary"])
        self.assertNotIn("http", r["summary"])

    def test_a_correct_file_turns_green(self):
        self.install_retroarch("swanstation")
        (self.system_dir).mkdir(parents=True)
        (self.system_dir / "scph5500.bin").write_bytes(ZEROS)
        r = self.report()
        self.assertTrue(r["ok"])
        self.assertEqual(["ok", "missing"], [f["state"] for f in r["files"]])

    def test_wrong_size_wrong_hash_wrong_region_and_wrong_case(self):
        self.install_retroarch("swanstation")
        self.system_dir.mkdir(parents=True)
        (self.system_dir / "scph5500.bin").write_bytes(b"short")
        self.assertIn("is 5 bytes; a correct dump is 524,288", self.report()["files"][0]["detail"])
        (self.system_dir / "scph5500.bin").write_bytes(b"\2" + ZEROS[1:])
        r = self.report()
        self.assertEqual("wrong", r["files"][0]["state"])
        self.assertIn("expected " + PLACEHOLDER[0].md5, r["files"][0]["detail"])
        self.assertIn("BIOS-sized file", r["summary"])
        (self.system_dir / "scph5500.bin").write_bytes(b"\1" + ZEROS[1:])  # the North America placeholder under Japan's name
        self.assertIn("rename it to scph5501.bin", self.report()["files"][0]["detail"])
        (self.system_dir / "scph5500.bin").unlink()
        (self.system_dir / "SCPH5500.BIN").write_bytes(ZEROS)
        r = self.report()
        self.assertFalse(r["ok"])
        self.assertIn("rename SCPH5500.BIN to scph5500.bin", r["files"][0]["detail"])

    def test_ps1_without_a_bios_prefers_the_hle_core_and_says_so(self):
        self.install_retroarch("swanstation", "pcsx_rearmed")
        front = xa.find_frontend(self.env)
        self.assertEqual("pcsx_rearmed", xa.choose_emulator(self.env, front, self.psx).id)
        r = self.report()
        self.assertTrue(r["hle"])
        self.assertIn("No BIOS needed", r["summary"])
        self.assertIn("lower compatibility", r["summary"])
        option = next(o for o in xa.emulator_options(self.env, front, self.psx) if o["id"] == "retroarch:pcsx_rearmed")
        self.assertIn("no BIOS needed, lower compatibility", option["label"])
        self.system_dir.mkdir(parents=True)
        (self.system_dir / "scph5500.bin").write_bytes(ZEROS)
        self.assertEqual("swanstation", xa.choose_emulator(self.env, front, self.psx).id)

    def test_an_unknown_revision_does_not_push_the_player_onto_hle(self):
        self.install_retroarch("swanstation", "pcsx_rearmed")
        self.system_dir.mkdir(parents=True)
        (self.system_dir / "scph1001.bin").write_bytes(b"\7" + ZEROS[1:])
        self.assertEqual("swanstation", xa.choose_emulator(self.env, xa.find_frontend(self.env), self.psx).id)

    def test_ps2_has_no_bios_free_option_and_is_checked_by_content(self):
        self.install_retroarch("pcsx2")
        ps2 = xa.SYSTEM_BY_ID["ps2"]
        r = self.report(ps2)
        self.assertEqual(str(self.system_dir / "pcsx2/bios"), r["dir"])
        self.assertFalse(r["ok"] or r["hle"])
        self.assertIn("no emulator or core has a BIOS-free mode", r["summary"])
        (self.system_dir / "pcsx2/bios").mkdir(parents=True)
        (self.system_dir / "pcsx2/bios/notabios.bin").write_bytes(bytes(1 << 20))
        self.assertEqual("wrong", self.report(ps2)["files"][0]["state"])
        (self.system_dir / "pcsx2/bios/mine.bin").write_bytes(fake_ps2_bios())
        r = self.report(ps2)
        self.assertTrue(r["ok"])
        self.assertIn("mine.bin: v1.60 Europe", r["summary"])

    def test_ps2_romver_rejects_other_files(self):
        p = self.touch(self.home / "x.bin", "RESET")
        self.assertIsNone(xa.ps2_romver(p))
        p.write_bytes(fake_ps2_bios(b"not a version!"))
        self.assertIsNone(xa.ps2_romver(p))
        self.assertIsNone(xa.ps2_romver(self.home / "missing.bin"))

    def test_standalone_pcsx2_reads_the_players_own_bios_folder(self):
        (self.home / "flatpak/app/net.pcsx2.PCSX2").mkdir(parents=True)
        self.touch(self.home / ".var/app/net.pcsx2.PCSX2/config/PCSX2/inis/PCSX2.ini", "[Folders]\nBios = /mnt/dumps/ps2\n")
        self.assertEqual("/mnt/dumps/ps2", self.report(xa.SYSTEM_BY_ID["ps2"])["dir"])

    def test_the_bios_step_appears_only_for_consoles_you_have_games_for(self):
        xa.setup(self.env)
        self.install_retroarch("swanstation", "snes9x")
        self.touch(self.games / "snes" / "Game.sfc")
        index = xa.Index(self.env.db_path)
        index.replace_library(xa.scan(self.env))
        self.assertEqual(3, len(xa.build_state(self.env, index, FakeSync())["checks"]))
        self.touch(self.games / "psx" / "Final Fantasy VII (Disc 1).chd")
        index.replace_library(xa.scan(self.env))
        state = xa.build_state(self.env, index, FakeSync())
        self.assertEqual(("bios-psx", False), (state["checks"][3]["key"], state["checks"][3]["ok"]))
        self.assertEqual([str(self.system_dir)], state["watch"])
        self.system_dir.mkdir(parents=True)
        (self.system_dir / "scph5501.bin").write_bytes(b"\1" + ZEROS[1:])
        self.assertTrue(xa.build_state(self.env, index, FakeSync())["checks"][3]["ok"])
        index.close()

    def test_the_real_table_is_well_formed(self):
        self.addCleanup(lambda: None)
        for files in xa.BIOS_FILES.values():
            for f in files:
                self.assertRegex(f.md5, r"^[0-9a-f]{32}$")
                self.assertEqual(f.name, f.name.lower())
