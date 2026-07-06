import importlib.util
import json
import os
import pathlib
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
