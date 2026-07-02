import importlib.util
import pathlib
import sys
import tempfile
import time
import unittest
from argparse import Namespace


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_benchmark.py"
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


def rule(evaluation, name):
    for item in evaluation["rules"]:
        if item["name"] == name:
            return item
    raise AssertionError(f"missing rule {name}")


if __name__ == "__main__":
    unittest.main()
