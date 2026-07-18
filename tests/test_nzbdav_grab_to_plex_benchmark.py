import importlib.util
import datetime as dt
import json
import pathlib
import stat
import sys
import tempfile
import unittest
import urllib.parse
from contextlib import redirect_stdout
import io


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_grab_to_plex_benchmark.py"
SPEC = importlib.util.spec_from_file_location("nzbdav_grab_to_plex_benchmark", SCRIPT_PATH)
benchmark = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = benchmark
SPEC.loader.exec_module(benchmark)


class AggregationTests(unittest.TestCase):
    def test_aggregate_reports_p50_p95_and_p99_for_completed_runs(self):
        artifacts = [
            run_artifact(1000.0),
            run_artifact(2000.0),
            run_artifact(3000.0),
        ]

        result = benchmark.aggregate_documents(artifacts)

        summary = result["metrics"]["sab_request_start_to_plex_basic_metadata_ready_ms"]
        self.assertEqual(summary["count"], 3)
        self.assertEqual(summary["p50"], 2000.0)
        self.assertEqual(summary["p95"], 2900.0)
        self.assertEqual(summary["p99"], 2980.0)
        accepted = result["metrics"]["sab_accepted_to_plex_basic_metadata_ready_ms"]
        self.assertEqual(accepted["p99"], 1490.0)

    def test_aggregate_excludes_failed_and_unrelated_artifacts(self):
        failed = run_artifact(1.0)
        failed["status"] = "failed"
        unrelated = run_artifact(2.0)
        unrelated["kind"] = "some-other-benchmark"
        non_isolated = run_artifact(2.5)
        non_isolated["measurement"] = {"valid": False, "isolated": False}
        forced_refresh = run_artifact(2.75)
        forced_refresh["measurement"] = {
            "valid": True,
            "isolated": True,
            "production_representative": False,
        }

        result = benchmark.aggregate_documents([
            run_artifact(3000.0),
            failed,
            unrelated,
            non_isolated,
            forced_refresh,
        ])

        self.assertEqual(result["runs_discovered"], 5)
        self.assertEqual(result["runs_included"], 1)
        self.assertEqual(result["runs_excluded"], 4)
        self.assertEqual(result["exclusion_reasons"], {
            "kind_mismatch": 1,
            "measurement_invalid": 1,
            "not_completed": 1,
            "not_production_representative": 1,
        })

    def test_aggregate_excludes_legacy_or_unprovenanced_artifacts_with_reasons(self):
        wrong_schema = run_artifact(1.0)
        wrong_schema["schema_version"] = 0
        missing_measurement = run_artifact(2.0)
        missing_measurement.pop("measurement")
        missing_valid = run_artifact(3.0)
        missing_valid["measurement"].pop("valid")
        missing_isolated = run_artifact(4.0)
        missing_isolated["measurement"].pop("isolated")
        missing_representative = run_artifact(5.0)
        missing_representative["measurement"].pop("production_representative")

        result = benchmark.aggregate_documents([
            run_artifact(6.0),
            wrong_schema,
            missing_measurement,
            missing_valid,
            missing_isolated,
            missing_representative,
        ])

        self.assertEqual(result["runs_included"], 1)
        self.assertEqual(result["runs_excluded"], 5)
        self.assertEqual(result["exclusion_reasons"], {
            "measurement_invalid": 2,
            "measurement_not_isolated": 1,
            "not_production_representative": 1,
            "schema_version_mismatch": 1,
        })

    def test_aggregate_rejects_malformed_metrics_and_stage_timing_with_bounded_reasons(self):
        artifacts = []

        def malformed(mutator):
            artifact = run_artifact(1000.0)
            mutator(artifact)
            artifacts.append(artifact)

        malformed(lambda artifact: artifact.__setitem__("schema_version", True))
        malformed(lambda artifact: artifact.__setitem__("schema_version", float(benchmark.RUN_SCHEMA_VERSION)))
        malformed(lambda artifact: artifact.__setitem__("durations_ms", []))
        malformed(lambda artifact: artifact.__setitem__("durations_ms", {}))
        malformed(lambda artifact: artifact["durations_ms"].pop(
            "sab_request_start_to_plex_basic_metadata_ready_ms"))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "sab_request_start_to_plex_basic_metadata_ready_ms", True))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "sab_request_start_to_plex_basic_metadata_ready_ms", -1.0))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "sab_request_start_to_plex_basic_metadata_ready_ms", float("nan")))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "sab_accepted_to_plex_basic_metadata_ready_ms", float("inf")))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "optional_stage_ms", float("nan")))
        malformed(lambda artifact: artifact.__setitem__("stages", {}))
        malformed(lambda artifact: artifact["stages"].pop("nzbdav_addfile_accepted"))
        malformed(lambda artifact: artifact["stages"]["plex_basic_metadata_ready"].__setitem__(
            "monotonic_seconds", float("nan")))
        malformed(lambda artifact: artifact["stages"]["nzbdav_addfile_accepted"].__setitem__(
            "monotonic_seconds",
            artifact["stages"]["plex_basic_metadata_ready"]["monotonic_seconds"] + 1.0))
        malformed(lambda artifact: artifact["durations_ms"].__setitem__(
            "sab_accepted_to_plex_basic_metadata_ready_ms", 1.0))

        result = benchmark.aggregate_documents([run_artifact(1000.0), *artifacts])

        self.assertEqual(result["runs_included"], 1)
        self.assertEqual(result["runs_excluded"], len(artifacts))
        self.assertEqual(result["exclusion_reasons"], {
            "durations_invalid": 2,
            "duration_value_invalid": 1,
            "primary_metric_invalid": 4,
            "primary_metric_mismatch": 1,
            "primary_metric_missing": 1,
            "schema_version_mismatch": 2,
            "stage_timing_invalid": 1,
            "stage_timing_misordered": 1,
            "stage_timing_missing": 2,
        })

    def test_aggregate_requires_complete_import_and_exact_plex_provenance(self):
        missing_arr_stage = run_artifact(1000.0)
        missing_arr_stage["stages"].pop("arr_history_imported")
        missing_arr_observation = run_artifact(1000.0)
        missing_arr_observation["observations"]["arr"]["history_import_observed"] = False
        missing_exact_plex_visibility = run_artifact(1000.0)
        missing_exact_plex_visibility["observations"]["plex"]["visibility"]["visible"] = False
        missing_completion_marker = run_artifact(1000.0)
        missing_completion_marker["stages"].pop("benchmark_completed")
        missing_nzo_id = run_artifact(1000.0)
        missing_nzo_id["run"]["nzo_id"] = None
        dry_run = run_artifact(1000.0)
        dry_run["dry_run"] = True

        result = benchmark.aggregate_documents([
            run_artifact(1000.0),
            missing_arr_stage,
            missing_arr_observation,
            missing_exact_plex_visibility,
            missing_completion_marker,
            missing_nzo_id,
            dry_run,
        ])

        self.assertEqual(result["runs_included"], 1)
        self.assertEqual(result["exclusion_reasons"], {
            "dry_run_not_false": 1,
            "pipeline_evidence_invalid": 2,
            "run_provenance_invalid": 1,
            "stage_timing_missing": 2,
        })

    def test_aggregate_rejects_forced_mode_inconsistent_with_representative_claim(self):
        forced_arr = run_artifact(1000.0)
        forced_arr["inputs"]["arr_refresh_mode"] = "forced"
        forced_arr["observations"]["arr"]["refresh_mode"] = "forced"
        forced_plex = run_artifact(1000.0)
        forced_plex["inputs"]["plex_scan_mode"] = "forced"
        forced_plex["observations"]["plex"]["scan_mode"] = "forced"

        result = benchmark.aggregate_documents([
            run_artifact(1000.0),
            forced_arr,
            forced_plex,
        ])

        self.assertEqual(result["runs_included"], 1)
        self.assertEqual(result["exclusion_reasons"], {
            "provenance_mode_mismatch": 2,
        })


class SecretAndRedactionTests(unittest.TestCase):
    def test_secret_can_be_loaded_from_file_without_becoming_an_artifact_input(self):
        with tempfile.TemporaryDirectory() as directory:
            secret_file = pathlib.Path(directory) / "api-key"
            secret_file.write_text("top-secret\n", encoding="utf-8")

            value = benchmark.resolve_secret(
                direct=None,
                file_arg=secret_file,
                env_name="TEST_API_KEY",
                environ={},
            )

        self.assertEqual(value, "top-secret")

    def test_secret_sources_are_ambiguous_when_direct_and_file_are_both_set(self):
        with self.assertRaisesRegex(ValueError, "multiple secret sources"):
            benchmark.resolve_secret(
                direct="top-secret",
                file_arg=pathlib.Path("unused"),
                env_name="TEST_API_KEY",
                environ={},
            )

    def test_secret_can_be_loaded_from_environment_file_pointer(self):
        with tempfile.TemporaryDirectory() as directory:
            secret_file = pathlib.Path(directory) / "token"
            secret_file.write_text("plex-secret", encoding="utf-8")

            value = benchmark.resolve_secret(
                direct=None,
                file_arg=None,
                env_name="PLEX_TOKEN",
                environ={"PLEX_TOKEN_FILE": str(secret_file)},
            )

        self.assertEqual(value, "plex-secret")

    def test_url_redaction_removes_userinfo_query_and_fragment(self):
        redacted = benchmark.redact_url("https://user:secret@example.test:123/api?apikey=hidden#fragment")

        self.assertEqual(redacted, "https://example.test:123/api")
        self.assertNotIn("secret", redacted)
        self.assertNotIn("hidden", redacted)

    def test_multipart_filename_strips_header_control_characters(self):
        self.assertEqual(
            benchmark.multipart_filename('evil"\r\nX-Injected: yes.nzb'),
            "evilX-Injected: yes.nzb",
        )


class PlexMetadataTests(unittest.TestCase):
    def test_exact_part_match_does_not_accept_suffix_or_substring(self):
        expected = "/media/Movies/Example (2026)/Example (2026).mkv"
        document = plex_page([
            plex_item("1", f"{expected}.partial", title="Example", year=2026),
            plex_item("2", expected, title="Example", year=2026),
        ])

        matches = benchmark.extract_exact_part_matches(document, [expected])

        self.assertEqual(matches[expected]["ratingKey"], "2")

    def test_basic_metadata_predicate_supports_nested_fields_and_zero_values(self):
        item = {
            "ratingKey": "9",
            "title": "Pilot",
            "index": 0,
            "Guid": [{"id": "tmdb://123"}],
        }

        ready, missing = benchmark.metadata_fields_ready(
            item,
            ["ratingKey", "title", "index", "Guid.id"],
        )

        self.assertTrue(ready)
        self.assertEqual(missing, [])

    def test_metadata_predicate_reports_empty_fields_as_missing(self):
        ready, missing = benchmark.metadata_fields_ready(
            {"ratingKey": "1", "title": "", "summary": None},
            ["ratingKey", "title", "summary", "thumb"],
        )

        self.assertFalse(ready)
        self.assertEqual(missing, ["title", "summary", "thumb"])


class BenchmarkRunnerTests(unittest.TestCase):
    def test_sonarr_run_records_distinct_end_to_end_stages_without_secrets(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="sonarr")
            config = make_config(nzb_path, arr_kind="sonarr", rich_fields=("summary",))

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "completed")
        self.assertEqual(artifact["measurement"], {
            "valid": True,
            "isolated": True,
            "production_representative": True,
            "observation_model": "serial_phase_polling",
            "intermediate_stage_timestamps_are_upper_bounds": True,
            "primary_origins": {
                "request_start": "nzbdav_addfile_request_started",
                "sab_accepted": "nzbdav_addfile_accepted",
            },
        })
        self.assertEqual(artifact["run"]["nzo_id"], PipelineTransport.NZO_ID)
        self.assertIsNone(artifact["run"]["arr_command_id"])
        self.assertEqual(artifact["inputs"]["arr_refresh_mode"], "observe")
        self.assertEqual(artifact["inputs"]["plex_scan_mode"], "observe")
        self.assertEqual(artifact["observations"]["arr"]["refresh_mode"], "observe")
        self.assertTrue(artifact["observations"]["arr"]["history_import_observed"])
        self.assertFalse(artifact["observations"]["arr"]["nzbdav_receipt_imported_observed"])
        self.assertEqual(
            artifact["observations"]["arr"]["nzbdav_receipt_measurement"],
            "not_measured",
        )
        self.assertEqual(artifact["observations"]["plex"]["scan_mode"], "observe")
        self.assertEqual(
            artifact["observations"]["plex"]["observer"]["strategy"],
            "recently_added_bounded_window",
        )
        self.assertEqual(artifact["observations"]["plex"]["observer"]["max_pages_per_poll"], 3)
        self.assertGreater(artifact["observations"]["plex"]["observer"]["requests"], 0)
        expected_stages = {
            "benchmark_started",
            "nzbdav_addfile_request_started",
            "nzbdav_addfile_accepted",
            "nzbdav_queue_seen",
            "nzbdav_history_completed",
            "arr_queue_seen",
            "arr_history_imported",
            "plex_item_visible",
            "plex_basic_metadata_ready",
            "plex_rich_metadata_ready",
            "benchmark_completed",
        }
        self.assertTrue(expected_stages.issubset(artifact["stages"]))
        self.assertNotIn("arr_command_accepted", artifact["stages"])
        self.assertNotIn("arr_command_completed", artifact["stages"])
        self.assertNotIn("plex_scan_accepted", artifact["stages"])
        arr_posts = [
            request
            for request in transport.requests
            if request.method == "POST" and urllib.parse.urlsplit(request.url).netloc == "arr.test"
        ]
        self.assertEqual(arr_posts, [])
        plex_refreshes = [
            request
            for request in transport.requests
            if "/refresh" in urllib.parse.urlsplit(request.url).path
        ]
        self.assertEqual(plex_refreshes, [])
        for timestamp in artifact["stages"].values():
            self.assertIn("utc", timestamp)
            self.assertIn("monotonic_seconds", timestamp)
            self.assertIn("elapsed_ms", timestamp)
        self.assertLessEqual(
            artifact["stages"]["plex_item_visible"]["monotonic_seconds"],
            artifact["stages"]["plex_basic_metadata_ready"]["monotonic_seconds"],
        )
        self.assertLessEqual(
            artifact["stages"]["plex_basic_metadata_ready"]["monotonic_seconds"],
            artifact["stages"]["plex_rich_metadata_ready"]["monotonic_seconds"],
        )
        self.assertNotIn("submission_to_plex_scan_accepted_ms", artifact["durations_ms"])
        self.assertIn("submission_to_plex_item_visible_ms", artifact["durations_ms"])
        self.assertIn("submission_to_plex_basic_metadata_ready_ms", artifact["durations_ms"])
        self.assertIn("submission_to_plex_rich_metadata_ready_ms", artifact["durations_ms"])
        self.assertIn("sab_request_start_to_plex_basic_metadata_ready_ms", artifact["durations_ms"])
        self.assertIn("sab_accepted_to_plex_basic_metadata_ready_ms", artifact["durations_ms"])
        serialized = json.dumps(artifact)
        for secret in ("nzbdav-secret", "arr-secret", "plex-secret"):
            self.assertNotIn(secret, serialized)

    def test_radarr_uses_movie_metadata_defaults(self):
        self.assertEqual(
            benchmark.default_basic_fields("radarr"),
            ("ratingKey", "title", "year"),
        )
        self.assertEqual(
            benchmark.default_basic_fields("sonarr"),
            ("ratingKey", "title", "grandparentTitle", "parentIndex", "index"),
        )

    def test_radarr_run_uses_movie_query_and_reaches_basic_metadata(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr")
            config = make_config(nzb_path, arr_kind="radarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "completed")
        plex_queries = [
            urllib.parse.parse_qs(urllib.parse.urlsplit(request.url).query)
            for request in transport.requests
            if urllib.parse.urlsplit(request.url).path.endswith("/all")
        ]
        self.assertTrue(plex_queries)
        self.assertTrue(all(query["type"] == ["1"] for query in plex_queries))
        self.assertTrue(all(query["sort"] == ["addedAt:desc"] for query in plex_queries))

    def test_forced_arr_refresh_is_explicit_and_non_representative(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr")
            base = make_config(nzb_path, arr_kind="radarr")
            config = benchmark.RunConfig(
                **{
                    **base.__dict__,
                    "force_arr_refresh": True,
                }
            )

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "completed")
        self.assertEqual(artifact["inputs"]["arr_refresh_mode"], "forced")
        self.assertEqual(artifact["observations"]["arr"]["refresh_mode"], "forced")
        self.assertFalse(artifact["measurement"]["production_representative"])
        self.assertEqual(artifact["run"]["arr_command_id"], 7)
        self.assertIn("arr_command_accepted", artifact["stages"])
        self.assertIn("arr_command_completed", artifact["stages"])
        arr_posts = [
            request
            for request in transport.requests
            if request.method == "POST" and urllib.parse.urlsplit(request.url).netloc == "arr.test"
        ]
        self.assertEqual(len(arr_posts), 1)
        self.assertEqual(urllib.parse.urlsplit(arr_posts[0].url).path, "/api/v3/command")

    def test_forced_plex_scan_is_explicit_and_non_representative(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr")
            base = make_config(nzb_path, arr_kind="radarr")
            config = benchmark.RunConfig(
                **{
                    **base.__dict__,
                    "force_plex_scan": True,
                }
            )

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "completed")
        self.assertEqual(artifact["inputs"]["plex_scan_mode"], "forced")
        self.assertEqual(artifact["observations"]["plex"]["scan_mode"], "forced")
        self.assertFalse(artifact["measurement"]["production_representative"])
        self.assertIn("plex_scan_accepted", artifact["stages"])
        plex_refreshes = [
            request
            for request in transport.requests
            if request.method == "GET" and "/refresh" in urllib.parse.urlsplit(request.url).path
        ]
        self.assertEqual(len(plex_refreshes), 1)

    def test_plex_pagination_uses_total_size_when_server_caps_page_size(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            config = make_config(nzb_path, arr_kind="radarr")
            transport = CappedPlexTransport(clock)
            runner = benchmark.BenchmarkRunner(config, transport=transport, clock=clock)

            matches = runner._find_plex_parts()

        self.assertIn(PipelineTransport.FINAL_FILE, matches)
        self.assertEqual(transport.starts, [0, 1])

    def test_dry_run_performs_read_only_validation_and_no_mutations(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr")
            config = make_config(nzb_path, arr_kind="radarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run(dry_run=True)

        self.assertEqual(artifact["status"], "validated")
        self.assertTrue(artifact["dry_run"])
        self.assertTrue(all(check["passed"] for check in artifact["checks"] if check["severity"] == "error"))
        self.assertFalse(any(request.method == "POST" for request in transport.requests))
        self.assertFalse(any("/refresh" in urllib.parse.urlsplit(request.url).path for request in transport.requests))
        self.assertTrue(any(
            urllib.parse.parse_qs(urllib.parse.urlsplit(request.url).query).get("mode") == ["fullstatus"]
            for request in transport.requests
        ))
        self.assertFalse(any(
            urllib.parse.parse_qs(urllib.parse.urlsplit(request.url).query).get("mode") == ["status"]
            for request in transport.requests
        ))
        unverified = next(check for check in artifact["checks"] if check["code"] == "mount_link_support_unverified")
        self.assertEqual(unverified["severity"], "warning")
        self.assertFalse(unverified["passed"])

    def test_preflight_rejects_paused_or_conflicting_nzbdav_work(self):
        cases = (
            ({"paused": True}, "nzbdav_queue_paused"),
            ({"jobs_active": 1}, "nzbdav_workload_not_isolated"),
            ({"worker_queues": {"download_ready": 1}}, "nzbdav_workload_not_isolated"),
        )
        for status_overrides, expected_code in cases:
            with self.subTest(status_overrides=status_overrides), temporary_nzb() as nzb_path:
                clock = FakeClock()
                transport = PipelineTransport(
                    clock,
                    arr_kind="radarr",
                    nzbdav_status_overrides=status_overrides,
                )
                artifact = benchmark.BenchmarkRunner(
                    make_config(nzb_path, arr_kind="radarr"),
                    transport=transport,
                    clock=clock,
                ).run(dry_run=True)

            self.assertEqual(artifact["status"], "failed")
            self.assertEqual(artifact["error"]["code"], expected_code)

    def test_preflight_rejects_impossible_rclone_fence_and_invalidation_backlog(self):
        cases = (
            ({"rclone_invalidations": {"remote_control_enabled": False}}, "rclone_visibility_fence_unavailable"),
            ({"rclone_invalidations": {"host_configured": False}}, "rclone_visibility_fence_unavailable"),
            ({"rclone_invalidations": {"whole_cache_visibility_fence_pending": True}}, "rclone_whole_cache_fence_pending"),
            ({"rclone_invalidations": {"pending": 1}}, "rclone_invalidations_pending"),
            ({"rclone_invalidations": {"failed": 1}}, "rclone_invalidations_failed"),
        )
        for status_overrides, expected_code in cases:
            with self.subTest(status_overrides=status_overrides), temporary_nzb() as nzb_path:
                clock = FakeClock()
                transport = PipelineTransport(
                    clock,
                    arr_kind="radarr",
                    nzbdav_status_overrides=status_overrides,
                )
                artifact = benchmark.BenchmarkRunner(
                    make_config(nzb_path, arr_kind="radarr"),
                    transport=transport,
                    clock=clock,
                ).run(dry_run=True)

            self.assertEqual(artifact["status"], "failed")
            self.assertEqual(artifact["error"]["code"], expected_code)

    def test_preflight_rejects_arr_import_backlog_and_invalid_arr_validation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            backlog_transport = PipelineTransport(
                clock,
                arr_kind="radarr",
                nzbdav_status_overrides={"arr_import_commands": {"retry": 1}},
            )
            backlog = benchmark.BenchmarkRunner(
                make_config(nzb_path, arr_kind="radarr"),
                transport=backlog_transport,
                clock=clock,
            ).run(dry_run=True)

        self.assertEqual(backlog["error"]["code"], "arr_import_backlog_present")

        validation_cases = (
            ({"instance_count": 0, "issues": []}, "arr_instance_missing"),
            ({
                "instance_count": 1,
                "issues": [{"severity": "error", "code": "bad-route", "message": "bad route"}],
            }, "arr_validation_failed"),
        )
        for arr_validation, expected_code in validation_cases:
            with self.subTest(arr_validation=arr_validation), temporary_nzb() as nzb_path:
                clock = FakeClock()
                transport = PipelineTransport(
                    clock,
                    arr_kind="radarr",
                    arr_validation=arr_validation,
                )
                artifact = benchmark.BenchmarkRunner(
                    make_config(nzb_path, arr_kind="radarr"),
                    transport=transport,
                    clock=clock,
                ).run(dry_run=True)

            self.assertEqual(artifact["status"], "failed")
            self.assertEqual(artifact["error"]["code"], expected_code)

    def test_preflight_rejects_existing_arr_queue_backlog(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr", arr_queue_backlog=True)

            artifact = benchmark.BenchmarkRunner(
                make_config(nzb_path, arr_kind="radarr"),
                transport=transport,
                clock=clock,
            ).run(dry_run=True)

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "arr_queue_not_empty")
        self.assertFalse(any(request.method == "POST" for request in transport.requests))

    def test_timed_plex_observer_uses_bounded_recent_window(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = LargePlexLibraryTransport(clock)
            runner = benchmark.BenchmarkRunner(
                make_config(nzb_path, arr_kind="radarr"),
                transport=transport,
                clock=clock,
            )

            matches = runner._find_plex_parts(observation_window=True)

        self.assertEqual(matches, {})
        self.assertEqual(transport.starts, [0, 100, 200])
        observer = runner.observations["plex"]["observer"]
        self.assertEqual(observer["strategy"], "recently_added_bounded_window")
        self.assertEqual(observer["max_pages_per_poll"], 3)
        self.assertEqual(observer["requests"], 3)
        self.assertEqual(observer["pages"], 3)

    def test_validation_rejects_arr_kind_mismatch_without_mutation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(
                clock,
                arr_kind="sonarr",
                reported_arr_kind="radarr",
            )
            config = make_config(nzb_path, arr_kind="sonarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run(dry_run=True)

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "arr_application_mismatch")
        self.assertFalse(any(request.method == "POST" for request in transport.requests))

    def test_validation_authenticates_to_nzbdav_without_mutation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(
                clock,
                arr_kind="radarr",
                invalid_nzbdav_key=True,
            )
            config = make_config(nzb_path, arr_kind="radarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run(dry_run=True)

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "nzbdav_unreachable")
        self.assertFalse(any(request.method == "POST" for request in transport.requests))

    def test_validation_rejects_wrong_plex_section_type_without_mutation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(
                clock,
                arr_kind="sonarr",
                reported_plex_type="movie",
            )
            config = make_config(nzb_path, arr_kind="sonarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run(dry_run=True)

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "plex_section_type_mismatch")
        self.assertFalse(any(request.method == "POST" for request in transport.requests))

    def test_validation_rejects_non_absolute_plex_part_before_mutation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr")
            base = make_config(nzb_path, arr_kind="radarr")
            config = benchmark.RunConfig(
                **{
                    **base.__dict__,
                    "plex_final_files": ("Example.mkv",),
                }
            )

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run(dry_run=True)

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "configuration_invalid")
        self.assertEqual(transport.requests, [])

    def test_existing_exact_plex_part_fails_before_addfile(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr", existing_part=True)
            config = make_config(nzb_path, arr_kind="radarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "plex_part_already_present")
        self.assertFalse(any(request.method == "POST" for request in transport.requests))

    def test_nzbdav_failure_stops_before_arr_or_plex_mutation(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="sonarr", nzbdav_failure=True)
            config = make_config(nzb_path, arr_kind="sonarr")

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "failed")
        self.assertEqual(artifact["error"]["code"], "nzbdav_history_failed")
        post_paths = [
            urllib.parse.urlsplit(request.url).path
            for request in transport.requests
            if request.method == "POST"
        ]
        self.assertEqual(post_paths, ["/api"])
        self.assertFalse(any("/refresh" in urllib.parse.urlsplit(request.url).path for request in transport.requests))

    def test_polling_timeout_is_deterministic_with_fake_clock(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr", never_complete=True)
            config = make_config(nzb_path, arr_kind="radarr", timeout_seconds=2.0)

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "timed_out")
        self.assertEqual(artifact["error"]["code"], "nzbdav_completion_timeout")
        self.assertGreaterEqual(clock.monotonic(), 2.0)

    def test_optional_rich_metadata_timeout_does_not_relabel_basic_success(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr", never_rich=True)
            config = make_config(nzb_path, arr_kind="radarr", rich_fields=("summary",))

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "completed")
        self.assertTrue(artifact["observations"]["plex"]["basic_metadata"]["ready"])
        self.assertFalse(artifact["observations"]["plex"]["rich_metadata"]["ready"])
        self.assertNotIn("plex_rich_metadata_ready", artifact["stages"])

    def test_required_rich_metadata_timeout_is_reported_separately(self):
        with temporary_nzb() as nzb_path:
            clock = FakeClock()
            transport = PipelineTransport(clock, arr_kind="radarr", never_rich=True)
            base = make_config(nzb_path, arr_kind="radarr", rich_fields=("summary",))
            config = benchmark.RunConfig(
                **{
                    **base.__dict__,
                    "require_rich_metadata": True,
                    "rich_timeout_seconds": 0.5,
                }
            )

            artifact = benchmark.BenchmarkRunner(config, transport=transport, clock=clock).run()

        self.assertEqual(artifact["status"], "timed_out")
        self.assertEqual(artifact["error"]["code"], "plex_rich_metadata_timeout")
        self.assertIn("plex_basic_metadata_ready", artifact["stages"])


class CliTests(unittest.TestCase):
    def test_parser_builds_run_config_from_secret_files(self):
        with temporary_nzb() as nzb_path, tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            key_paths = {}
            for name, value in (
                ("nzbdav", "nzbdav-secret"),
                ("arr", "arr-secret"),
                ("plex", "plex-secret"),
            ):
                key_path = root / name
                key_path.write_text(value, encoding="utf-8")
                key_paths[name] = key_path
            args = benchmark.build_parser().parse_args([
                "run",
                "--nzbdav-url", "https://nzbdav.test",
                "--nzbdav-api-key-file", str(key_paths["nzbdav"]),
                "--nzb", str(nzb_path),
                "--category", "movies",
                "--arr-kind", "radarr",
                "--arr-url", "https://arr.test",
                "--arr-api-key-file", str(key_paths["arr"]),
                "--plex-url", "https://plex.test",
                "--plex-token-file", str(key_paths["plex"]),
                "--plex-section-id", "2",
                "--plex-final-file", PipelineTransport.FINAL_FILE,
                "--dry-run",
            ])

            config = benchmark.config_from_args(args, environ={})

        self.assertEqual(config.nzbdav_api_key, "nzbdav-secret")
        self.assertEqual(config.arr_api_key, "arr-secret")
        self.assertEqual(config.plex_token, "plex-secret")
        self.assertEqual(config.basic_fields, ("ratingKey", "title", "year"))
        self.assertFalse(config.force_arr_refresh)
        self.assertFalse(config.force_plex_scan)
        self.assertTrue(args.dry_run)

    def test_force_arr_refresh_cli_flag_is_explicit_opt_in(self):
        with temporary_nzb() as nzb_path:
            args = benchmark.build_parser().parse_args([
                "run",
                "--nzbdav-url", "https://nzbdav.test",
                "--nzbdav-api-key", "nzbdav-secret",
                "--nzb", str(nzb_path),
                "--category", "movies",
                "--arr-kind", "radarr",
                "--arr-url", "https://arr.test",
                "--arr-api-key", "arr-secret",
                "--plex-url", "https://plex.test",
                "--plex-token", "plex-secret",
                "--plex-section-id", "2",
                "--plex-final-file", PipelineTransport.FINAL_FILE,
                "--force-arr-refresh",
            ])

            config = benchmark.config_from_args(args, environ={})

        self.assertTrue(config.force_arr_refresh)

    def test_force_plex_scan_cli_flag_is_explicit_opt_in(self):
        with temporary_nzb() as nzb_path:
            args = benchmark.build_parser().parse_args([
                "run",
                "--nzbdav-url", "https://nzbdav.test",
                "--nzbdav-api-key", "nzbdav-secret",
                "--nzb", str(nzb_path),
                "--category", "movies",
                "--arr-kind", "radarr",
                "--arr-url", "https://arr.test",
                "--arr-api-key", "arr-secret",
                "--plex-url", "https://plex.test",
                "--plex-token", "plex-secret",
                "--plex-section-id", "2",
                "--plex-final-file", PipelineTransport.FINAL_FILE,
                "--force-plex-scan",
            ])

            config = benchmark.config_from_args(args, environ={})

        self.assertTrue(config.force_plex_scan)

    def test_aggregate_command_reads_existing_artifact_directory_and_writes_private_json(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            inputs = root / "runs"
            inputs.mkdir()
            for index, duration in enumerate((1000.0, 2000.0, 3000.0)):
                (inputs / f"run-{index}.json").write_text(
                    json.dumps(run_artifact(duration)),
                    encoding="utf-8",
                )
            output = root / "aggregate.json"

            with redirect_stdout(io.StringIO()):
                exit_code = benchmark.main(["aggregate", str(inputs), "--output", str(output)])

            document = json.loads(output.read_text(encoding="utf-8"))
            permissions = stat.S_IMODE(output.stat().st_mode)

        self.assertEqual(exit_code, 0)
        self.assertEqual(document["runs_included"], 3)
        self.assertEqual(
            document["metrics"]["submission_to_plex_basic_metadata_ready_ms"]["p99"],
            2980.0,
        )
        self.assertEqual(permissions & 0o077, 0)


class FakeClock:
    def __init__(self):
        self.value = 0.0
        self.start = dt.datetime(2026, 7, 12, 10, 0, tzinfo=dt.timezone.utc)

    def monotonic(self):
        return self.value

    def utcnow(self):
        return self.start + dt.timedelta(seconds=self.value)

    def sleep(self, seconds):
        self.value += seconds

    def advance(self, seconds):
        self.value += seconds


class PipelineTransport:
    NZO_ID = "11111111-2222-3333-4444-555555555555"
    FINAL_FILE = "/media/Movies/Example (2026)/Example (2026).mkv"

    def __init__(
        self,
        clock,
        *,
        arr_kind,
        existing_part=False,
        nzbdav_failure=False,
        never_complete=False,
        never_rich=False,
        reported_arr_kind=None,
        invalid_nzbdav_key=False,
        reported_plex_type=None,
        nzbdav_status_overrides=None,
        arr_validation=None,
        arr_queue_backlog=False,
    ):
        self.clock = clock
        self.arr_kind = arr_kind
        self.existing_part = existing_part
        self.nzbdav_failure = nzbdav_failure
        self.never_complete = never_complete
        self.never_rich = never_rich
        self.reported_arr_kind = reported_arr_kind or arr_kind
        self.invalid_nzbdav_key = invalid_nzbdav_key
        self.reported_plex_type = reported_plex_type or ("show" if arr_kind == "sonarr" else "movie")
        self.nzbdav_status_overrides = nzbdav_status_overrides or {}
        self.arr_validation = arr_validation or {"instance_count": 1, "issues": []}
        self.arr_queue_backlog = arr_queue_backlog
        self.requests = []
        self.counts = {}

    def send(self, request):
        self.requests.append(request)
        self.clock.advance(0.05)
        parsed = urllib.parse.urlsplit(request.url)
        query = urllib.parse.parse_qs(parsed.query)
        key = (request.method, parsed.netloc, parsed.path, query.get("mode", [None])[0])
        self.counts[key] = self.counts.get(key, 0) + 1
        count = self.counts[key]

        if parsed.netloc == "nzbdav.test" and query.get("mode") == ["version"]:
            return response({"status": True, "version": "test"})
        if parsed.netloc == "nzbdav.test" and query.get("mode") == ["fullstatus"]:
            if self.invalid_nzbdav_key:
                return response({"status": False, "error": "unauthorized"}, status=401)
            status = healthy_full_status()
            deep_update(status, self.nzbdav_status_overrides)
            return response({"status": status})
        if parsed.netloc == "nzbdav.test" and parsed.path == "/api/arr/validation":
            return response(self.arr_validation)
        if parsed.netloc == "arr.test" and parsed.path == "/api/v3/system/status":
            return response({"appName": self.reported_arr_kind, "version": "test"})
        if parsed.netloc == "plex.test" and parsed.path == "/identity":
            return response({"MediaContainer": {"machineIdentifier": "test"}})
        if parsed.netloc == "plex.test" and parsed.path == "/library/sections":
            return response({
                "MediaContainer": {
                    "Directory": [{
                        "key": "2",
                        "type": self.reported_plex_type,
                        "title": "Test Library",
                    }]
                }
            })
        if parsed.netloc == "plex.test" and parsed.path.endswith("/all"):
            if self.existing_part:
                return response(plex_page([self._plex_item(rich=True)]))
            plex_poll = self.counts.get(("plex-pages",), 0) + 1
            self.counts[("plex-pages",)] = plex_poll
            if not any(
                item.method == "POST" and urllib.parse.urlsplit(item.url).netloc == "nzbdav.test"
                for item in self.requests
            ):
                return response(plex_page([]))
            if plex_poll == 2:
                return response(plex_page([self._plex_item(visible_only=True)]))
            if plex_poll == 3:
                return response(plex_page([self._plex_item(rich=False)]))
            return response(plex_page([self._plex_item(rich=not self.never_rich)]))
        if parsed.netloc == "nzbdav.test" and query.get("mode") == ["addfile"]:
            return response({"status": True, "nzo_ids": [self.NZO_ID]})
        if parsed.netloc == "nzbdav.test" and query.get("mode") == ["queue"]:
            return response({
                "status": True,
                "queue": {"slots": [{"nzo_id": self.NZO_ID, "status": "Downloading"}]},
            })
        if parsed.netloc == "nzbdav.test" and query.get("mode") == ["history"]:
            if self.never_complete:
                return response({"status": True, "history": {"slots": []}})
            history_status = "Failed" if self.nzbdav_failure else "Completed"
            return response({
                "status": True,
                "history": {
                    "slots": [{
                        "nzo_id": self.NZO_ID,
                        "status": history_status,
                        "storage": "/downloads/example",
                        "fail_message": "provider failed" if self.nzbdav_failure else "",
                    }]
                },
            })
        if parsed.netloc == "arr.test" and parsed.path == "/api/v3/command" and request.method == "POST":
            return response({"id": 7, "name": "RefreshMonitoredDownloads", "status": "queued"}, status=201)
        if parsed.netloc == "arr.test" and parsed.path == "/api/v3/command/7":
            return response({"id": 7, "name": "RefreshMonitoredDownloads", "status": "completed"})
        if parsed.netloc == "arr.test" and parsed.path == "/api/v3/queue":
            if "downloadId" not in query:
                return response({
                    "totalRecords": 1 if self.arr_queue_backlog else 0,
                    "records": ([{"downloadId": "existing"}] if self.arr_queue_backlog else []),
                })
            return response({"records": [{"downloadId": self.NZO_ID, "status": "completed"}]})
        if parsed.netloc == "arr.test" and parsed.path == "/api/v3/history":
            return response({
                "records": [{
                    "id": 99,
                    "downloadId": self.NZO_ID,
                    "eventType": "downloadFolderImported",
                    "data": {"importedPath": self.FINAL_FILE},
                }]
            })
        if parsed.netloc == "plex.test" and parsed.path.endswith("/refresh"):
            return benchmark.HttpResponse(status=200, headers={}, body=b"")
        raise AssertionError(f"unexpected request: {request.method} {request.url}")

    def _plex_item(self, *, visible_only=False, rich=False):
        item = plex_item("42", self.FINAL_FILE)
        if visible_only:
            return item
        if self.arr_kind == "sonarr":
            item.update({
                "title": "Pilot",
                "grandparentTitle": "Example",
                "parentIndex": 1,
                "index": 1,
            })
        else:
            item.update({"title": "Example", "year": 2026})
        if rich:
            item["summary"] = "A test item."
        return item


class CappedPlexTransport:
    def __init__(self, clock):
        self.clock = clock
        self.starts = []

    def send(self, request):
        self.clock.advance(0.01)
        headers = request.headers
        start = int(headers["X-Plex-Container-Start"])
        self.starts.append(start)
        if start == 0:
            item = plex_item("1", "/media/Movies/Other/Other.mkv", title="Other", year=2025)
        else:
            item = plex_item(
                "2",
                PipelineTransport.FINAL_FILE,
                title="Example",
                year=2026,
            )
        return response({
            "MediaContainer": {
                "size": 1,
                "totalSize": 2,
                "offset": start,
                "Metadata": [item],
            }
        })


class LargePlexLibraryTransport:
    def __init__(self, clock):
        self.clock = clock
        self.starts = []

    def send(self, request):
        self.clock.advance(0.01)
        start = int(request.headers["X-Plex-Container-Start"])
        size = int(request.headers["X-Plex-Container-Size"])
        self.starts.append(start)
        items = [
            plex_item(
                str(index),
                f"/media/Movies/Other {index}/Other {index}.mkv",
                title=f"Other {index}",
                year=2025,
            )
            for index in range(start, start + size)
        ]
        return response({
            "MediaContainer": {
                "size": size,
                "totalSize": 10_000,
                "offset": start,
                "Metadata": items,
            }
        })


def response(document, *, status=200):
    return benchmark.HttpResponse(
        status=status,
        headers={"content-type": "application/json"},
        body=json.dumps(document).encode("utf-8"),
    )


def healthy_full_status():
    return {
        "paused": False,
        "jobs": 0,
        "jobs_active": 0,
        "queue_status": "Idle",
        "worker_queues": {
            "download_active": 0,
            "download_waiting": 0,
            "download_ready": 0,
            "download_retry": 0,
            "download_quarantined": 0,
            "verify_active": 0,
            "verify_ready": 0,
            "verify_retry": 0,
            "verify_quarantined": 0,
            "repair_active": 0,
            "repair_action_needed": 0,
            "repair_ready": 0,
            "repair_retry": 0,
            "repair_quarantined": 0,
        },
        "rclone_invalidations": {
            "pending": 0,
            "ready": 0,
            "failed": 0,
            "visibility_fence_required": True,
            "whole_cache_visibility_fence_pending": False,
            "remote_control_enabled": True,
            "host_configured": True,
            "last_successful_configured_call_at": "2026-07-12T09:59:00Z",
        },
        "arr_import_commands": {
            "pending": 0,
            "waiting_for_invalidation": 0,
            "executing": 0,
            "retry": 0,
            "no_route": 0,
            "quarantined": 0,
        },
        "mount": {
            "type": "rclone",
            "state": "external-unverified",
            "ready": False,
        },
    }


def deep_update(target, updates):
    for key, value in updates.items():
        if isinstance(value, dict) and isinstance(target.get(key), dict):
            deep_update(target[key], value)
        else:
            target[key] = value
    return target


class temporary_nzb:
    def __enter__(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = pathlib.Path(self.directory.name) / "example.nzb"
        self.path.write_text(
            '<?xml version="1.0"?><nzb><file subject="Example"><segments>'
            '<segment bytes="100" number="1">abc@example</segment>'
            "</segments></file></nzb>",
            encoding="utf-8",
        )
        return self.path

    def __exit__(self, exc_type, exc_value, traceback):
        self.directory.cleanup()


def make_config(
    nzb_path,
    *,
    arr_kind,
    rich_fields=(),
    timeout_seconds=10.0,
):
    return benchmark.RunConfig(
        nzbdav_base_url="https://nzbdav.test",
        nzbdav_api_key="nzbdav-secret",
        nzb_path=nzb_path,
        category="tv" if arr_kind == "sonarr" else "movies",
        arr_kind=arr_kind,
        arr_base_url="https://arr.test",
        arr_api_key="arr-secret",
        plex_base_url="https://plex.test",
        plex_token="plex-secret",
        plex_section_id="2",
        plex_final_files=(PipelineTransport.FINAL_FILE,),
        basic_fields=benchmark.default_basic_fields(arr_kind),
        rich_fields=rich_fields,
        require_rich_metadata=False,
        poll_interval_seconds=0.5,
        timeout_seconds=timeout_seconds,
        rich_timeout_seconds=2.0,
        http_timeout_seconds=5.0,
        plex_page_size=100,
        allow_existing_plex_item=False,
    )


def run_artifact(duration_ms):
    request_start = 10.0
    sab_accepted = request_start + duration_ms / 2000.0
    metadata_ready = request_start + duration_ms / 1000.0
    nzbdav_completed = sab_accepted + (metadata_ready - sab_accepted) * 0.25
    arr_imported = sab_accepted + (metadata_ready - sab_accepted) * 0.5
    plex_visible = sab_accepted + (metadata_ready - sab_accepted) * 0.75
    return {
        "schema_version": benchmark.RUN_SCHEMA_VERSION,
        "kind": "nzbdav-grab-to-plex-run",
        "status": "completed",
        "dry_run": False,
        "measurement": {
            "valid": True,
            "isolated": True,
            "production_representative": True,
        },
        "run": {
            "nzo_id": PipelineTransport.NZO_ID,
            "arr_command_id": None,
        },
        "inputs": {
            "arr_refresh_mode": "observe",
            "plex_scan_mode": "observe",
            "plex_final_files": [PipelineTransport.FINAL_FILE],
        },
        "observations": {
            "nzbdav": {"history_status": "completed"},
            "arr": {
                "refresh_mode": "observe",
                "history_event_types": ["downloadFolderImported"],
                "history_import_observed": True,
                "imported_paths": [PipelineTransport.FINAL_FILE],
            },
            "plex": {
                "scan_mode": "observe",
                "visibility": {
                    "exact_part_files": [PipelineTransport.FINAL_FILE],
                    "visible": True,
                },
                "basic_metadata": {"ready": True},
            },
        },
        "durations_ms": {
            "submission_to_plex_basic_metadata_ready_ms": duration_ms,
            "sab_request_start_to_plex_basic_metadata_ready_ms": duration_ms,
            "sab_accepted_to_plex_basic_metadata_ready_ms": duration_ms / 2.0,
        },
        "stages": {
            "benchmark_started": {"monotonic_seconds": request_start - 1.0},
            "nzbdav_addfile_request_started": {"monotonic_seconds": request_start},
            "nzbdav_addfile_accepted": {"monotonic_seconds": sab_accepted},
            "nzbdav_history_completed": {"monotonic_seconds": nzbdav_completed},
            "arr_history_imported": {"monotonic_seconds": arr_imported},
            "plex_item_visible": {"monotonic_seconds": plex_visible},
            "plex_basic_metadata_ready": {"monotonic_seconds": metadata_ready},
            "benchmark_completed": {"monotonic_seconds": metadata_ready + 0.001},
        },
    }


def plex_page(items):
    return {
        "MediaContainer": {
            "size": len(items),
            "totalSize": len(items),
            "Metadata": items,
        }
    }


def plex_item(rating_key, part_file, **metadata):
    return {
        "ratingKey": rating_key,
        "Media": [{"Part": [{"file": part_file}]}],
        **metadata,
    }


if __name__ == "__main__":
    unittest.main()
