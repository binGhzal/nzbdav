import importlib.util
import pathlib
import sys
import unittest


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
        self.assertFalse(evaluation["rules"][2]["passed"])

    def test_evaluate_rejects_resource_regression_over_ten_percent(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.21, rss=1000, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evaluation["rules"][3]["passed"])

    def test_evaluate_rejects_failed_correctness_checks(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=False)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evaluation["rules"][5]["passed"])

    def test_evaluate_rejects_failed_baseline_correctness_checks(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=False)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evaluation["rules"][1]["passed"])

    def test_evaluate_rejects_incomparable_inputs(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        candidate["inputs"]["runs"] = 10

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evaluation["rules"][0]["passed"])
        self.assertEqual(evaluation["rules"][0]["mismatches"][0]["field"], "runs")

    def test_evaluate_rejects_changed_fail_closed_paths(self):
        baseline = benchmark_doc(seek_p95=100, cpu=2.0, rss=1000, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=2.0, rss=1000, checks=True)
        baseline["inputs"]["fail_closed_paths"] = ["/blocked/file.mkv"]
        candidate["inputs"]["fail_closed_paths"] = []

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertFalse(evaluation["accepted"])
        self.assertFalse(evaluation["rules"][0]["passed"])
        self.assertEqual(evaluation["rules"][0]["mismatches"][0]["field"], "fail_closed_paths")

    def test_resource_metrics_are_best_effort_when_missing(self):
        baseline = benchmark_doc(seek_p95=100, cpu=None, rss=None, checks=True)
        candidate = benchmark_doc(seek_p95=75, cpu=None, rss=None, checks=True)

        evaluation = nzbdav_benchmark.evaluate_acceptance(baseline, candidate)

        self.assertTrue(evaluation["accepted"])
        self.assertTrue(evaluation["rules"][3]["passed"])
        self.assertTrue(evaluation["rules"][4]["passed"])

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

    return {
        "scenario": "test",
        "inputs": {
            "paths": ["/content/file.mkv"],
            "seek_count": 5,
            "seek_offsets": [],
            "sequential_bytes": 67108864,
            "runs": 5,
            "fail_closed_paths": [],
        },
        "metrics": {"seek_latency_ms": {"p95": seek_p95}},
        "resources": {"summary": {"total": total}},
        "checks": {"passed": checks},
    }


if __name__ == "__main__":
    unittest.main()
