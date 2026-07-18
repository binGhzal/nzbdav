import importlib.util
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_safe_rclone_up.py"
SPEC = importlib.util.spec_from_file_location("nzbdav_safe_rclone_up", SCRIPT_PATH)
nzbdav_safe_rclone_up = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = nzbdav_safe_rclone_up
SPEC.loader.exec_module(nzbdav_safe_rclone_up)


class SafeRcloneUpTests(unittest.TestCase):
    def test_fingerprint_is_stable_for_same_compose_and_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "rclone.conf"
            path.write_text("config", encoding="utf-8")

            first, first_payload = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [path])
            second, second_payload = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [path])

            self.assertEqual(first, second)
            self.assertEqual(first_payload, second_payload)

    def test_fingerprint_changes_when_watched_file_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "rclone.conf"
            path.write_text("before", encoding="utf-8")
            before, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [path])

            path.write_text("after", encoding="utf-8")
            after, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [path])

            self.assertNotEqual(before, after)

    def test_fingerprint_ignores_watched_file_path_when_content_matches(self):
        with tempfile.TemporaryDirectory() as directory:
            first_path = pathlib.Path(directory) / "first" / "rclone.conf"
            second_path = pathlib.Path(directory) / "second" / "rclone.conf"
            first_path.parent.mkdir()
            second_path.parent.mkdir()
            first_path.write_text("same config", encoding="utf-8")
            second_path.write_text("same config", encoding="utf-8")

            first, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [first_path])
            second, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [second_path])

            self.assertEqual(first, second)

    def test_fingerprint_ignores_watched_file_order(self):
        with tempfile.TemporaryDirectory() as directory:
            first_path = pathlib.Path(directory) / "rclone.conf"
            second_path = pathlib.Path(directory) / ".env"
            first_path.write_text("rclone config", encoding="utf-8")
            second_path.write_text("env config", encoding="utf-8")

            first, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [first_path, second_path])
            second, _ = nzbdav_safe_rclone_up.compute_fingerprint("services: {}", [second_path, first_path])

            self.assertEqual(first, second)

    def test_should_apply_skips_unchanged_state_unless_forced(self):
        state = {"fingerprint": "same"}

        self.assertFalse(nzbdav_safe_rclone_up.should_apply(state, "same", force=False))
        self.assertTrue(nzbdav_safe_rclone_up.should_apply(state, "same", force=True))
        self.assertTrue(nzbdav_safe_rclone_up.should_apply(state, "different", force=False))

    def test_save_state_is_atomic_private_and_digest_only(self):
        with tempfile.TemporaryDirectory() as directory:
            state_path = pathlib.Path(directory) / "state.json"
            state_path.write_text(
                json.dumps({"payload": {"compose_config": "PASSWORD=hunter2"}}),
                encoding="utf-8")
            state_path.chmod(0o644)

            nzbdav_safe_rclone_up.save_state(state_path, "a" * 64)

            persisted = state_path.read_text(encoding="utf-8")
            state = json.loads(persisted)
            self.assertEqual(
                {"format_version", "fingerprint", "updated_at"},
                set(state))
            self.assertEqual(1, state["format_version"])
            self.assertEqual("a" * 64, state["fingerprint"])
            self.assertNotIn("hunter2", persisted)
            self.assertNotIn("compose_config", persisted)
            self.assertEqual(
                0o600,
                stat.S_IMODE(state_path.stat().st_mode))
            self.assertEqual([state_path.name], sorted(path.name for path in state_path.parent.iterdir()))

    def test_save_state_replace_failure_preserves_old_state_and_removes_private_temp(self):
        with tempfile.TemporaryDirectory() as directory:
            state_path = pathlib.Path(directory) / "state.json"
            original = b'{"fingerprint":"old"}\n'
            state_path.write_bytes(original)
            original_replace = nzbdav_safe_rclone_up.os.replace

            def fail_replace(source, destination, **kwargs):
                raise OSError("simulated replace failure")

            try:
                nzbdav_safe_rclone_up.os.replace = fail_replace
                with self.assertRaisesRegex(OSError, "simulated replace failure"):
                    nzbdav_safe_rclone_up.save_state(state_path, "b" * 64)
            finally:
                nzbdav_safe_rclone_up.os.replace = original_replace

            self.assertEqual(original, state_path.read_bytes())
            self.assertEqual([state_path.name], sorted(path.name for path in state_path.parent.iterdir()))

    def test_unchanged_legacy_state_is_rewritten_without_compose_or_secret(self):
        with tempfile.TemporaryDirectory() as directory:
            project_dir = pathlib.Path(directory)
            compose_config = "services:\n  rclone:\n    environment:\n      PASSWORD: hunter2\n"
            fingerprint, _ = nzbdav_safe_rclone_up.compute_fingerprint(compose_config, [])
            state_path = project_dir / ".nzbdav-rclone-state.json"
            state_path.write_text(
                json.dumps({
                    "fingerprint": fingerprint,
                    "payload": {"compose_config": compose_config},
                }),
                encoding="utf-8")
            state_path.chmod(0o644)
            commands: list[list[str]] = []

            def fake_run_command(command: list[str], cwd: pathlib.Path, capture: bool):
                commands.append(command)
                if command[-2:] == ["config", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout=compose_config, stderr="")
                self.fail(f"unchanged state must not inspect or mutate the container: {command}")

            original_run_command = nzbdav_safe_rclone_up.run_command
            try:
                nzbdav_safe_rclone_up.run_command = fake_run_command
                exit_code = nzbdav_safe_rclone_up.main([
                    "--project-dir", str(project_dir),
                    "--compose-file", "compose.yml",
                ])
            finally:
                nzbdav_safe_rclone_up.run_command = original_run_command

            self.assertEqual(0, exit_code)
            self.assertEqual(1, len(commands))
            persisted = state_path.read_text(encoding="utf-8")
            self.assertNotIn("hunter2", persisted)
            self.assertNotIn("payload", json.loads(persisted))
            self.assertEqual(0o600, stat.S_IMODE(state_path.stat().st_mode))

    def test_load_state_rejects_symlink_without_opening_target(self):
        if os.name == "nt":
            self.skipTest("symlink creation is not generally available to unprivileged Windows tests")

        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            target = root / "target.json"
            target.write_text('{"fingerprint":"target"}\n', encoding="utf-8")
            state_path = root / "state.json"
            state_path.symlink_to(target)

            with self.assertRaisesRegex(ValueError, "regular file"):
                nzbdav_safe_rclone_up.load_state(state_path)

            self.assertEqual('{"fingerprint":"target"}\n', target.read_text(encoding="utf-8"))

    def test_compose_up_command_never_forces_recreate(self):
        command = nzbdav_safe_rclone_up.build_compose_up_command(
            "docker compose",
            [pathlib.Path("compose.yml")],
            "nzbdav_rclone")

        self.assertEqual(
            command,
            ["docker", "compose", "-f", "compose.yml", "up", "-d", "nzbdav_rclone"])
        self.assertNotIn("--force-recreate", command)

    def test_main_adopts_matching_running_container_without_compose_up(self):
        with tempfile.TemporaryDirectory() as directory:
            project_dir = pathlib.Path(directory)
            watch_file = project_dir / "rclone.conf"
            watch_file.write_text("rclone config", encoding="utf-8")
            started_at = datetime.now(timezone.utc)
            older = (started_at - timedelta(minutes=5)).timestamp()
            watch_file.touch()
            watch_file.chmod(0o600)
            os.utime(watch_file, (older, older))
            commands: list[list[str]] = []

            def fake_run_command(command: list[str], cwd: pathlib.Path, capture: bool):
                commands.append(command)
                if command[-2:] == ["config", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="services: {}\n", stderr="")
                if command[-3:] == ["config", "--hash", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="compose-hash\n", stderr="")
                if command[-3:] == ["ps", "-q", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="container-id\n", stderr="")
                if command == ["docker", "inspect", "container-id"]:
                    payload = [{
                        "Config": {
                            "Labels": {
                                "com.docker.compose.config-hash": "compose-hash"
                            }
                        },
                        "State": {
                            "Running": True,
                            "StartedAt": started_at.isoformat().replace("+00:00", "Z")
                        }
                    }]
                    return subprocess.CompletedProcess(command, 0, stdout=json.dumps(payload), stderr="")
                if "up" in command:
                    self.fail(f"rclone sidecar should not be touched when live config is unchanged: {command}")
                raise AssertionError(f"unexpected command: {command}")

            original_run_command = nzbdav_safe_rclone_up.run_command
            try:
                nzbdav_safe_rclone_up.run_command = fake_run_command

                exit_code = nzbdav_safe_rclone_up.main([
                    "--project-dir", str(project_dir),
                    "--compose-file", "compose.yml",
                    "--watch-file", "rclone.conf",
                ])
            finally:
                nzbdav_safe_rclone_up.run_command = original_run_command

            self.assertEqual(0, exit_code)
            self.assertTrue((project_dir / ".nzbdav-rclone-state.json").exists())
            self.assertNotIn("up", [part for command in commands for part in command])

    def test_main_runs_compose_up_when_watched_file_is_newer_than_container(self):
        with tempfile.TemporaryDirectory() as directory:
            project_dir = pathlib.Path(directory)
            watch_file = project_dir / "rclone.conf"
            watch_file.write_text("updated rclone config", encoding="utf-8")
            started_at = datetime.now(timezone.utc) - timedelta(minutes=5)
            newer = (started_at + timedelta(minutes=1)).timestamp()
            os.utime(watch_file, (newer, newer))
            commands: list[list[str]] = []
            up_was_called = False

            def fake_run_command(command: list[str], cwd: pathlib.Path, capture: bool):
                nonlocal up_was_called
                commands.append(command)
                if command[-2:] == ["config", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="services: {}\n", stderr="")
                if command[-3:] == ["config", "--hash", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="compose-hash\n", stderr="")
                if command[-3:] == ["ps", "-q", "nzbdav_rclone"]:
                    return subprocess.CompletedProcess(command, 0, stdout="container-id\n", stderr="")
                if command == ["docker", "inspect", "container-id"]:
                    payload = [{
                        "Config": {
                            "Labels": {
                                "com.docker.compose.config-hash": "compose-hash"
                            }
                        },
                        "State": {
                            "Running": True,
                            "StartedAt": started_at.isoformat().replace("+00:00", "Z")
                        }
                    }]
                    return subprocess.CompletedProcess(command, 0, stdout=json.dumps(payload), stderr="")
                if command[-3:] == ["up", "-d", "nzbdav_rclone"]:
                    up_was_called = True
                    return subprocess.CompletedProcess(command, 0, stdout="", stderr="")
                raise AssertionError(f"unexpected command: {command}")

            original_run_command = nzbdav_safe_rclone_up.run_command
            try:
                nzbdav_safe_rclone_up.run_command = fake_run_command

                exit_code = nzbdav_safe_rclone_up.main([
                    "--project-dir", str(project_dir),
                    "--compose-file", "compose.yml",
                    "--watch-file", "rclone.conf",
                ])
            finally:
                nzbdav_safe_rclone_up.run_command = original_run_command

            self.assertEqual(0, exit_code)
            self.assertTrue(up_was_called)
            self.assertIn("up", [part for command in commands for part in command])


if __name__ == "__main__":
    unittest.main()
