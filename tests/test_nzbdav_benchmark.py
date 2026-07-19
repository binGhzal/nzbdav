import importlib.util
import pathlib
import sys
import tempfile
import time
import traceback
import unittest
from argparse import Namespace
from unittest import mock


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_benchmark.py"
TESTS_DIR = pathlib.Path(__file__).resolve().parent
for import_path in (TESTS_DIR, SCRIPT_PATH.parent):
    if str(import_path) not in sys.path:
        sys.path.insert(0, str(import_path))

from protocol_base_contract_cases import (  # noqa: E402
    INVALID_PROTOCOL_BASES,
    SAFE_BASE_ERROR,
    SAFE_PATH_ERROR,
    VALID_PROTOCOL_BASES,
)

SPEC = importlib.util.spec_from_file_location("nzbdav_benchmark", SCRIPT_PATH)
nzbdav_benchmark = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = nzbdav_benchmark
SPEC.loader.exec_module(nzbdav_benchmark)


class BenchmarkEvaluatorTests(unittest.TestCase):
    def test_percentile_interpolates(self):
        self.assertEqual(nzbdav_benchmark.percentile([10, 20, 30], 50), 20)
        self.assertEqual(nzbdav_benchmark.percentile([10, 20, 30], 95), 29)

    def test_evaluate_accepts_candidate_that_meets_gate(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=80, cpu=2.2, rss=1100, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertTrue(evaluation["accepted"])
        self.assertTrue(all(rule["passed"] for rule in evaluation["rules"]))

    def test_evaluate_rejects_candidate_without_twenty_percent_seek_improvement(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=81, cpu=2.0, rss=1000, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "p95 seek latency improves at least 20%")["passed"])

    def test_evaluate_rejects_resource_regression_over_ten_percent(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.21, rss=1000, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "CPU not worse by more than 10%")["passed"])

    def test_evaluate_rejects_failed_correctness_checks(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=False)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "correctness and fail-closed checks pass")["passed"])

    def test_evaluate_rejects_failed_baseline_correctness_checks(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=False)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "baseline correctness checks pass")["passed"])

    def test_evaluate_rejects_incomparable_inputs(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        candidate["inputs"]["runs"] = 10

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "benchmark inputs are comparable")["passed"])
        self.assertEqual(rule(evaluation, "benchmark inputs are comparable")["mismatches"][0]["field"], "runs")

    def test_evaluate_rejects_changed_fail_closed_paths(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        baseline["inputs"]["fail_closed_paths"] = ["/blocked/file.mkv"]
        candidate["inputs"]["fail_closed_paths"] = []

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "benchmark inputs are comparable")["passed"])
        self.assertEqual(
            rule(evaluation, "benchmark inputs are comparable")["mismatches"][0]["field"],
            "fail_closed_paths",
        )

    def test_evaluate_rejects_mixed_transports(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        candidate["inputs"]["transport"] = "filesystem"

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "benchmark inputs are comparable")["passed"])
        self.assertEqual(rule(evaluation, "benchmark inputs are comparable")["mismatches"][0]["field"], "transport")

    def test_evaluate_rejects_missing_resource_metrics(self):
        baseline = benchmark_doc(seek_p95=100, cpu=None, rss=None, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=None, rss=None, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(rule(evaluation, "CPU not worse by more than 10%")["passed"])
        self.assertFalse(rule(evaluation, "RSS not worse by more than 10%")["passed"])

    def test_evaluate_rejects_missing_rclone_process_resource_evidence(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        del baseline["resources"]["summary"]["sources"]["rclone_process"]

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        evidence = rule(evaluation, "resource evidence includes full compared stacks")
        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evidence["passed"])
        self.assertEqual(evidence["missing"][0]["name"], "rclone process")

    def test_evaluate_rejects_missing_candidate_nzbdav_resource_evidence(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        del candidate["resources"]["summary"]["sources"]["nzbdav_process"]
        del candidate["resources"]["summary"]["sources"]["nzbdav_status"]

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        evidence = rule(evaluation, "resource evidence includes full compared stacks")
        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evidence["passed"])
        self.assertEqual(evidence["missing"][0]["name"], "nzbdav/DFS process/status")

    def test_resource_total_counts_nzbdav_status_and_process_once(self):
        document = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        document["resources"]["summary"]["sources"]["nzbdav_process"] = {
            "cpu_cores_max": 1.0,
            "rss_bytes_max": 1000,
        }
        document["resources"]["summary"]["sources"]["nzbdav_status"] = {
            "cpu_cores_max": 4.0,
            "rss_bytes_max": 3000,
        }
        document["resources"]["summary"]["sources"]["rclone_process"] = {
            "cpu_cores_max": 0.5,
            "rss_bytes_max": 500,
        }

        self.assertEqual(nzbdav_benchmark.resource_total_metric(document, "cpu_cores_max"), 4.5)
        self.assertEqual(nzbdav_benchmark.resource_total_metric(document, "rss_bytes_max"), 3500)

    def test_summarize_resources_deduplicates_nzbdav_sources_in_total(self):
        summary = nzbdav_benchmark.summarize_resources([
            {
                "nzbdav_process": {"available": True, "cpu_percent": 100, "rss_bytes": 1000},
                "nzbdav_status": {"available": True, "process_cpu_cores": 4.0, "working_set_bytes": 3000},
                "rclone_process": {"available": True, "cpu_percent": 50, "rss_bytes": 500},
            }
        ])

        self.assertEqual(summary["total"]["cpu_cores_max"], 4.5)
        self.assertEqual(summary["total"]["rss_bytes_max"], 3500)

    def test_seek_result_rejects_200_range_response_and_does_not_sample(self):
        checks, seek_samples, operation_samples = [], [], []
        result = nzbdav_benchmark.HttpResult(
            status=200,
            elapsed_ms=50,
            first_byte_ms=10,
            bytes_read=1,
            headers={"content-range": "bytes 100-100/1000"},
        )

        nzbdav_benchmark.record_seek_result(
            checks, seek_samples, operation_samples, "/content/file.mkv", 100, result, 0
        )

        self.assertFalse(checks[0]["passed"])
        self.assertEqual(seek_samples, [])
        self.assertEqual(operation_samples[0]["path"], "/content/file.mkv")

    def test_seek_result_rejects_wrong_content_range_start_and_does_not_sample(self):
        checks, seek_samples, operation_samples = [], [], []
        result = nzbdav_benchmark.HttpResult(
            status=206,
            elapsed_ms=50,
            first_byte_ms=10,
            bytes_read=1,
            headers={"content-range": "bytes 99-99/1000"},
        )

        nzbdav_benchmark.record_seek_result(
            checks, seek_samples, operation_samples, "/content/file.mkv", 100, result, 0
        )

        self.assertFalse(checks[0]["passed"])
        self.assertEqual(seek_samples, [])

    def test_seek_result_accepts_206_matching_content_range_and_samples(self):
        checks, seek_samples, operation_samples = [], [], []
        result = nzbdav_benchmark.HttpResult(
            status=206,
            elapsed_ms=50,
            first_byte_ms=10,
            bytes_read=1,
            headers={"content-range": "bytes 100-100/1000"},
        )

        nzbdav_benchmark.record_seek_result(
            checks, seek_samples, operation_samples, "/content/file.mkv", 100, result, 0
        )

        self.assertTrue(checks[0]["passed"])
        self.assertEqual(seek_samples, [10])

    def test_filesystem_seek_result_accepts_matching_content_range_and_samples(self):
        checks, seek_samples, operation_samples = [], [], []
        result = nzbdav_benchmark.HttpResult(
            status=0,
            elapsed_ms=20,
            first_byte_ms=5,
            bytes_read=1,
            headers={"content-range": "bytes 100-100/1000"},
        )

        nzbdav_benchmark.record_seek_result(
            checks,
            seek_samples,
            operation_samples,
            "/content/file.mkv",
            100,
            result,
            0,
            nzbdav_benchmark.TRANSPORT_FILESYSTEM,
        )

        self.assertTrue(checks[0]["passed"])
        self.assertEqual(seek_samples, [5])

    def test_rclone_cat_defaults_false_for_programmatic_namespace(self):
        self.assertFalse(nzbdav_benchmark.rclone_cat_enabled(Namespace()))

    def test_rclone_cat_reads_explicit_namespace_value(self):
        self.assertTrue(nzbdav_benchmark.rclone_cat_enabled(Namespace(rclone_cat=True)))

    def test_root_and_nested_protocol_bases_keep_logical_requests_below_protocol(self):
        for configured, canonical in VALID_PROTOCOL_BASES:
            with self.subTest(configured=configured):
                normalized = nzbdav_benchmark.normalize_nzbdav_protocol_base(configured)
                self.assertEqual(normalized, canonical)
                self.assertEqual(
                    nzbdav_benchmark.join_url(normalized, "/content/movies/file.mkv"),
                    f"{canonical}/content/movies/file.mkv",
                )
                self.assertEqual(
                    nzbdav_benchmark.join_url(normalized, "api"),
                    f"{canonical}/api",
                )

    def test_http_path_cannot_escape_or_reset_the_protocol_base(self):
        invalid_paths = (
            "https://other.example/content/file.mkv",
            "//other.example/content/file.mkv",
            "/content/file.mkv?download=true",
            "/content/file.mkv#fragment",
            "../content/file.mkv",
            "/../content/file.mkv",
            "/content/../secret",
            "/content//file.mkv",
            "/content/%2e%2e/secret",
            "/content/%252e%252e/secret",
            "/content/%25252e%25252e/secret",
            "/content/%252fsecret",
            "/content/%25252fsecret",
            "/content/%255csecret",
            "/content/%25255csecret",
            "/content/%25AB",
            "/content/%FF-invalid-utf8.mkv",
            "/content/%252525252541",
            "/content%2ffile.mkv",
            "/content%5cfile.mkv",
            "/content/raw\nline.mkv",
            "/content/raw\rline.mkv",
            "/content/raw\x00line.mkv",
            "/content/encoded%0aline.mkv",
            "/content/encoded%0dline.mkv",
            "/content/encoded%00line.mkv",
            "/content/encoded%7fline.mkv",
        )

        for path in invalid_paths:
            with self.subTest(path=path):
                with self.assertRaisesRegex(ValueError, rf"^{SAFE_PATH_ERROR}$"):
                    nzbdav_benchmark.join_url(
                        "https://example.test/nzbdav/protocol",
                        path,
                    )

    def test_http_path_preserves_encoded_media_filename_segments(self):
        self.assertEqual(
            nzbdav_benchmark.join_url(
                "https://example.test/nzbdav/protocol",
                "/content/My%20Film/%E2%98%83.mkv",
            ),
            "https://example.test/nzbdav/protocol/content/My%20Film/%E2%98%83.mkv",
        )

    def test_http_path_control_boundary_matches_frontend_request_policy(self):
        base_url = "https://example.test/nzbdav/protocol"
        accepted_paths = (
            "/content/raw\u0080name.mkv",
            "/content/encoded%C2%80name.mkv",
        )
        rejected_paths = (
            "/content/raw\x7fname.mkv",
            "/content/encoded%7Fname.mkv",
            "/content/raw\u0085whitespace.mkv",
        )

        for path in accepted_paths:
            with self.subTest(path=path):
                try:
                    joined = nzbdav_benchmark.join_url(base_url, path)
                except ValueError as error:
                    self.fail(f"frontend-safe C1 path was rejected: {error}")
                self.assertEqual(joined, f"{base_url}{path}")

        for path in rejected_paths:
            with self.subTest(path=path):
                with self.assertRaisesRegex(ValueError, rf"^{SAFE_PATH_ERROR}$"):
                    nzbdav_benchmark.join_url(base_url, path)

    def test_http_path_preserves_encoded_literal_percent_in_media_filename(self):
        paths = (
            "/content/100%25.mkv",
            "/content/%2541.mkv",
            "/content/%2525literal.mkv",
        )

        for path in paths:
            with self.subTest(path=path):
                try:
                    joined = nzbdav_benchmark.join_url(
                        "https://example.test/nzbdav/protocol",
                        path,
                    )
                except ValueError as error:
                    self.fail(f"safe terminating literal-percent path was rejected: {error}")
                self.assertEqual(joined, f"https://example.test/nzbdav/protocol{path}")

    def test_malformed_suffix_traceback_suppresses_input_and_decode_context(self):
        path = "/content/%FF-suffix-trace-canary.mkv"

        with self.assertRaises(ValueError) as captured:
            nzbdav_benchmark.join_url(
                "https://example.test/nzbdav/protocol",
                path,
            )

        rendered = "".join(traceback.format_exception(captured.exception))
        self.assertEqual(str(captured.exception), SAFE_PATH_ERROR)
        self.assertEqual(rendered.count(SAFE_PATH_ERROR), 1)
        self.assertNotIn(path, rendered)
        self.assertNotIn("suffix-trace-canary", rendered)
        self.assertNotIn("UnicodeDecodeError", rendered)
        self.assertNotIn("direct cause", rendered)

    def test_join_url_remains_generic_for_rclone_rc_endpoints(self):
        self.assertEqual(
            nzbdav_benchmark.join_url("http://127.0.0.1:5572", "/core/stats"),
            "http://127.0.0.1:5572/core/stats",
        )

    def test_run_help_distinguishes_http_suffixes_from_filesystem_paths(self):
        parser = nzbdav_benchmark.build_parser()
        run_parser = next(action for action in parser._actions if action.dest == "command").choices["run"]
        help_text = run_parser.format_help()

        self.assertIn("logical suffix below the NzbDAV protocol base", help_text)
        self.assertNotIn("absolute URL", help_text)

    def test_invalid_http_protocol_base_fails_before_any_probe(self):
        for name, configured in INVALID_PROTOCOL_BASES:
            args = benchmark_namespace(
                transport=nzbdav_benchmark.TRANSPORT_HTTP,
                base_url=configured,
            )
            with self.subTest(name=name):
                with mock.patch.object(
                    nzbdav_benchmark,
                    "probe_path",
                    return_value=nzbdav_benchmark.HttpResult(
                        status=200,
                        elapsed_ms=1,
                        first_byte_ms=1,
                        bytes_read=1,
                        headers={},
                    ),
                ) as probe_path, mock.patch.object(
                    nzbdav_benchmark,
                    "nzbdav_status_snapshot",
                    return_value=None,
                ) as status_snapshot:
                    with self.assertRaisesRegex(SystemExit, rf"^{SAFE_BASE_ERROR}$"):
                        nzbdav_benchmark.run_benchmark(args)

                probe_path.assert_not_called()
                status_snapshot.assert_not_called()

    def test_invalid_base_cli_traceback_suppresses_input_and_helper_exception(self):
        configured = "https://example.test:benchmark-port-trace-canary/protocol"
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_HTTP,
            base_url=configured,
        )

        with self.assertRaises(SystemExit) as captured:
            nzbdav_benchmark.run_benchmark(args)

        rendered = "".join(traceback.format_exception(captured.exception))
        self.assertEqual(str(captured.exception), SAFE_BASE_ERROR)
        self.assertEqual(rendered.count(SAFE_BASE_ERROR), 1)
        self.assertNotIn(configured, rendered)
        self.assertNotIn("benchmark-port-trace-canary", rendered)
        self.assertNotIn("ValueError", rendered)
        self.assertNotIn("direct cause", rendered)

    def test_http_prevalidates_every_effective_path_before_any_side_effect(self):
        valid = "/content/valid.mkv"
        invalid = "/content/%FF-path-prevalidation-canary.mkv"
        cases = (
            ("primary", {"paths": [valid, invalid]}),
            ("fail-closed", {"fail_closed_paths": [valid, invalid]}),
            ("explicit-parallel", {"parallel_count": 2, "parallel_paths": [valid, invalid]}),
            ("fallback-parallel", {"parallel_count": 2, "paths": [valid, invalid]}),
        )
        successful = nzbdav_benchmark.HttpResult(
            status=200,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )

        for name, changes in cases:
            args = benchmark_namespace(
                transport=nzbdav_benchmark.TRANSPORT_HTTP,
                base_url="https://example.test/protocol",
            )
            for field, value in changes.items():
                setattr(args, field, value)

            with self.subTest(name=name):
                with (
                    mock.patch.object(nzbdav_benchmark, "auth_headers", return_value={}) as auth_headers,
                    mock.patch.object(nzbdav_benchmark, "probe_path", return_value=successful) as probe_path,
                    mock.patch.object(nzbdav_benchmark, "nzbdav_status_snapshot", return_value=None) as status_snapshot,
                    mock.patch.object(nzbdav_benchmark, "rclone_rc_snapshot", return_value=None) as rc_snapshot,
                    mock.patch.object(nzbdav_benchmark, "run_parallel_range_probe", return_value=None) as parallel_probe,
                    mock.patch.object(nzbdav_benchmark, "request_file") as file_read,
                ):
                    with self.assertRaisesRegex(SystemExit, rf"^{SAFE_PATH_ERROR}$") as captured:
                        nzbdav_benchmark.run_benchmark(args)

                rendered = "".join(traceback.format_exception(captured.exception))
                self.assertEqual(rendered.count(SAFE_PATH_ERROR), 1)
                self.assertNotIn(invalid, rendered)
                self.assertNotIn("path-prevalidation-canary", rendered)
                self.assertNotIn("ValueError", rendered)
                self.assertNotIn("direct cause", rendered)
                auth_headers.assert_not_called()
                probe_path.assert_not_called()
                status_snapshot.assert_not_called()
                rc_snapshot.assert_not_called()
                parallel_probe.assert_not_called()
                file_read.assert_not_called()

    def test_surplus_explicit_parallel_path_is_ignored_before_parallel_probe(self):
        active_paths = [
            "/content/parallel-one.mkv",
            "/content/parallel-two.mkv",
        ]
        surplus_invalid = "/content/%FF-surplus-parallel-canary.mkv"
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_HTTP,
            base_url="https://example.test/protocol",
        )
        args.parallel_count = len(active_paths)
        args.parallel_paths = [*active_paths, surplus_invalid]
        successful = nzbdav_benchmark.HttpResult(
            status=200,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )

        with (
            mock.patch.object(nzbdav_benchmark, "probe_path", return_value=successful),
            mock.patch.object(nzbdav_benchmark, "nzbdav_status_snapshot", return_value=None),
            mock.patch.object(nzbdav_benchmark, "rclone_rc_snapshot", return_value=None),
            mock.patch.object(nzbdav_benchmark, "run_parallel_range_probe", return_value=None) as parallel_probe,
        ):
            try:
                nzbdav_benchmark.run_benchmark(args)
            except SystemExit as error:
                self.fail(f"inactive surplus parallel path was rejected: {error}")

        parallel_probe.assert_called_once()
        self.assertEqual(parallel_probe.call_args.args[1], active_paths)
        self.assertNotIn(surplus_invalid, parallel_probe.call_args.args[1])

    def test_supplied_filesystem_protocol_base_validates_before_any_file_probe(self):
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_FILESYSTEM,
            base_url="https://example.test/not-protocol",
        )
        with mock.patch.object(nzbdav_benchmark, "probe_path") as probe_path:
            with self.assertRaisesRegex(SystemExit, rf"^{SAFE_BASE_ERROR}$"):
                nzbdav_benchmark.run_benchmark(args)

        probe_path.assert_not_called()

    def test_http_artifact_labels_canonical_protocol_base_and_preserves_alias(self):
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_HTTP,
            base_url="https://example.test/nzbdav/protocol/",
        )
        result = nzbdav_benchmark.HttpResult(
            status=200,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )
        with mock.patch.object(
            nzbdav_benchmark,
            "probe_path",
            return_value=result,
        ), mock.patch.object(
            nzbdav_benchmark,
            "nzbdav_status_snapshot",
            return_value=None,
        ):
            document = nzbdav_benchmark.run_benchmark(args)

        self.assertEqual(
            document["inputs"]["nzbdav_protocol_base"],
            "https://example.test/nzbdav/protocol",
        )
        self.assertEqual(
            document["inputs"]["base_url"],
            document["inputs"]["nzbdav_protocol_base"],
        )

    def test_ipv6_http_artifact_preserves_normalized_protocol_base_unchanged(self):
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_HTTP,
            base_url="http://[::1]:3000/protocol/",
        )
        result = nzbdav_benchmark.HttpResult(
            status=200,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )
        with mock.patch.object(
            nzbdav_benchmark,
            "probe_path",
            return_value=result,
        ), mock.patch.object(
            nzbdav_benchmark,
            "nzbdav_status_snapshot",
            return_value=None,
        ):
            document = nzbdav_benchmark.run_benchmark(args)

        self.assertEqual(
            document["inputs"]["nzbdav_protocol_base"],
            "http://[::1]:3000/protocol",
        )
        self.assertEqual(
            document["inputs"]["base_url"],
            document["inputs"]["nzbdav_protocol_base"],
        )

    def test_filesystem_prevalidates_every_effective_path_before_any_side_effect(self):
        valid = "/content/valid.mkv"
        invalid = "/content/../filesystem-prevalidation-canary.mkv"
        cases = (
            ("primary", {"paths": [valid, invalid]}),
            ("fail-closed", {"fail_closed_paths": [valid, invalid]}),
            ("explicit-parallel", {"parallel_count": 2, "parallel_paths": [valid, invalid]}),
            ("fallback-parallel", {"parallel_count": 2, "paths": [valid, invalid]}),
        )
        successful = nzbdav_benchmark.HttpResult(
            status=0,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )

        for name, changes in cases:
            args = benchmark_namespace(
                transport=nzbdav_benchmark.TRANSPORT_FILESYSTEM,
                base_url=None,
            )
            for field, value in changes.items():
                setattr(args, field, value)

            with self.subTest(name=name):
                with (
                    mock.patch.object(nzbdav_benchmark, "auth_headers", return_value={}) as auth_headers,
                    mock.patch.object(nzbdav_benchmark, "probe_path", return_value=successful) as probe_path,
                    mock.patch.object(nzbdav_benchmark, "nzbdav_status_snapshot", return_value=None) as status_snapshot,
                    mock.patch.object(nzbdav_benchmark, "rclone_rc_snapshot", return_value=None) as rc_snapshot,
                    mock.patch.object(nzbdav_benchmark, "run_parallel_range_probe", return_value=None) as parallel_probe,
                    mock.patch.object(nzbdav_benchmark, "request_file") as file_read,
                ):
                    with self.assertRaises(ValueError):
                        nzbdav_benchmark.run_benchmark(args)

                auth_headers.assert_not_called()
                probe_path.assert_not_called()
                status_snapshot.assert_not_called()
                rc_snapshot.assert_not_called()
                parallel_probe.assert_not_called()
                file_read.assert_not_called()

    def test_filesystem_prevalidation_preserves_no_root_absolute_and_file_urls(self):
        args = benchmark_namespace(
            transport=nzbdav_benchmark.TRANSPORT_FILESYSTEM,
            base_url=None,
        )
        args.mount_root = None
        args.paths = [
            "/disposable/absolute.mkv",
            "file:///disposable/file-url.mkv",
        ]
        successful = nzbdav_benchmark.HttpResult(
            status=0,
            elapsed_ms=1,
            first_byte_ms=1,
            bytes_read=1,
            headers={},
        )

        with mock.patch.object(
            nzbdav_benchmark,
            "probe_path",
            return_value=successful,
        ) as probe_path, mock.patch.object(
            nzbdav_benchmark,
            "nzbdav_status_snapshot",
            return_value=None,
        ), mock.patch.object(
            nzbdav_benchmark,
            "rclone_rc_snapshot",
            return_value=None,
        ):
            document = nzbdav_benchmark.run_benchmark(args)

        self.assertTrue(document["checks"]["passed"])
        self.assertEqual(probe_path.call_count, 2)

    def test_run_benchmark_reads_filesystem_mount_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            content = root / "content"
            content.mkdir()
            file_path = content / "file.mkv"
            file_path.write_bytes(bytes(range(256)) * 8192)

            document = nzbdav_benchmark.run_benchmark(Namespace(
                transport=nzbdav_benchmark.TRANSPORT_FILESYSTEM,
                scenario="rclone",
                base_url=None,
                mount_root=str(root),
                paths=["/content/file.mkv"],
                webdav_user=None,
                webdav_pass=None,
                api_key=None,
                rclone_rc_url=None,
                rclone_rc_user=None,
                rclone_rc_pass=None,
                rclone_remote=None,
                range_probe_bytes=1024 * 1024,
                plex_part_urls=[],
                parallel_count=1,
                parallel_paths=[],
                nzbdav_pid=None,
                rclone_pid=None,
                runs=1,
                seek_count=2,
                seek_offsets=[],
                sequential_bytes=4096,
                timeout_seconds=5,
                fail_closed_paths=["/content/missing.mkv"],
            ))

        self.assertTrue(document["checks"]["passed"])
        self.assertEqual(document["inputs"]["transport"], "filesystem")
        self.assertEqual(document["metrics"]["seek_latency_ms"]["count"], 2)
        self.assertEqual(document["metrics"]["sequential_throughput_mib_s"]["count"], 1)

    def test_filesystem_mount_root_rejects_parent_escape(self):
        with self.assertRaises(ValueError):
            nzbdav_benchmark.join_filesystem_path("/mnt/nzbdav", "/content/../outside.mkv")

    def test_filesystem_alarm_interrupts_blocking_operation(self):
        if not nzbdav_benchmark.supports_filesystem_alarm(0.01):
            self.skipTest("POSIX interval timers are not available")

        started = time.monotonic()
        with self.assertRaises(TimeoutError):
            with nzbdav_benchmark.filesystem_deadline(0.01, "/content/blocked.mkv"):
                time.sleep(1)
        self.assertLess(time.monotonic() - started, 0.5)

    def test_redacts_credentials_and_query_strings_from_urls_and_paths(self):
        self.assertEqual(
            nzbdav_benchmark.redact_path("https://user:secret@example.test:8443/content/file.mkv?token=abc"),
            "https://example.test:8443/content/file.mkv",
        )
        self.assertEqual(nzbdav_benchmark.redact_path("/content/file.mkv?apikey=secret"), "/content/file.mkv")


def benchmark_doc(seek_p95, cpu, rss, checks):
    total = {}
    if cpu is not None:
        total["cpu_cores_max"] = cpu
    if rss is not None:
        total["rss_bytes_max"] = rss

    sources = {}
    if cpu is not None and rss is not None:
        source = {"cpu_cores_max": cpu, "rss_bytes_max": rss}
        sources = {
            "nzbdav_process": dict(source),
            "nzbdav_status": dict(source),
            "rclone_process": dict(source),
        }

    return {
        "scenario": "test",
        "inputs": {
            "paths": ["/content/file.mkv"],
            "transport": "http",
            "seek_count": 5,
            "seek_offsets": [],
            "sequential_bytes": 67108864,
            "runs": 5,
            "fail_closed_paths": [],
        },
        "metrics": {"seek_latency_ms": {"p95": seek_p95}},
        "resources": {"summary": {"total": total, "sources": sources}},
        "checks": {"passed": checks},
    }


def benchmark_namespace(*, transport, base_url):
    return Namespace(
        transport=transport,
        scenario="contract",
        base_url=base_url,
        mount_root="/disposable-mount" if transport == nzbdav_benchmark.TRANSPORT_FILESYSTEM else None,
        paths=["/content/file.mkv"],
        webdav_user=None,
        webdav_pass=None,
        api_key=None,
        rclone_rc_url=None,
        rclone_rc_user=None,
        rclone_rc_pass=None,
        rclone_remote=None,
        range_probe_bytes=1,
        plex_part_urls=[],
        parallel_count=0,
        parallel_paths=[],
        nzbdav_pid=None,
        rclone_pid=None,
        runs=0,
        seek_count=0,
        seek_offsets=[],
        sequential_bytes=1,
        timeout_seconds=1,
        fail_closed_paths=[],
    )


def rule(evaluation, name):
    for item in evaluation["rules"]:
        if item["name"] == name:
            return item
    raise AssertionError(f"missing rule {name}")


if __name__ == "__main__":
    unittest.main()
