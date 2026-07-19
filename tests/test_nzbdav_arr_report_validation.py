import importlib.util
import json
import pathlib
import sys
import tempfile
import traceback
import unittest
from unittest import mock


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_arr_report_validation.py"
TESTS_DIR = pathlib.Path(__file__).resolve().parent
for import_path in (TESTS_DIR, SCRIPT_PATH.parent):
    if str(import_path) not in sys.path:
        sys.path.insert(0, str(import_path))

from protocol_base_contract_cases import (  # noqa: E402
    INVALID_PROTOCOL_BASES,
    SAFE_BASE_ERROR,
    VALID_PROTOCOL_BASES,
)

SPEC = importlib.util.spec_from_file_location("nzbdav_arr_report_validation", SCRIPT_PATH)
nzbdav_arr_report_validation = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = nzbdav_arr_report_validation
SPEC.loader.exec_module(nzbdav_arr_report_validation)


class ArrReportValidationTests(unittest.TestCase):
    def test_fetch_documents_does_not_request_general_config(self):
        requested_paths = []

        def request_json(_base_url, path, _api_key):
            requested_paths.append(path)
            return {}

        with (
            mock.patch.object(nzbdav_arr_report_validation, "request_json", side_effect=request_json),
            mock.patch.object(
                nzbdav_arr_report_validation,
                "request_config",
                create=True,
                side_effect=AssertionError("general config must not be requested with the public API key"),
            ),
        ):
            documents = nzbdav_arr_report_validation.fetch_documents(
                "https://nzbdav.example/protocol",
                "public-key",
            )

        self.assertNotIn("config", documents)
        self.assertEqual(
            [
                "/api/arr/validation",
                "/api/arr/search-nudges?limit=500",
                "/api/arr/correlations?limit=500",
                "/api?mode=fullstatus",
            ],
            requested_paths,
        )

    def test_root_and_nested_protocol_bases_construct_every_request_below_protocol(self):
        expected_suffixes = (
            "/api/arr/validation",
            "/api/arr/search-nudges?limit=500",
            "/api/arr/correlations?limit=500",
            "/api?mode=fullstatus",
        )

        for configured, canonical in VALID_PROTOCOL_BASES:
            requested_urls = []

            def request_json(base_url, path, _api_key):
                requested_urls.append(nzbdav_arr_report_validation.join_url(base_url, path))
                return {}

            with self.subTest(configured=configured), mock.patch.object(
                nzbdav_arr_report_validation,
                "request_json",
                side_effect=request_json,
            ):
                nzbdav_arr_report_validation.fetch_documents(configured, "x")

            self.assertEqual(
                requested_urls,
                [f"{canonical}{suffix}" for suffix in expected_suffixes],
            )

    def test_invalid_protocol_base_fails_before_fetch_or_artifact_creation(self):
        clean_documents = documents(
            sonarr=True,
            radarr=True,
            search_mode="report",
            duplicate_behavior="increment",
            coverage=100,
            failed_nudges=0,
            issues=[],
        )

        for name, configured in INVALID_PROTOCOL_BASES:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                output_dir = pathlib.Path(directory) / "artifacts"
                with mock.patch.object(
                    nzbdav_arr_report_validation,
                    "fetch_documents",
                    return_value=clean_documents,
                ) as fetch_documents:
                    with self.assertRaisesRegex(SystemExit, rf"^{SAFE_BASE_ERROR}$"):
                        nzbdav_arr_report_validation.main([
                            "--base-url",
                            configured,
                            "--api-key",
                            "x",
                            "--output-dir",
                            str(output_dir),
                        ])

                fetch_documents.assert_not_called()
                self.assertFalse(output_dir.exists())

    def test_artifact_labels_the_canonical_value_and_preserves_the_deprecated_alias(self):
        clean_documents = documents(
            sonarr=True,
            radarr=True,
            search_mode="report",
            duplicate_behavior="increment",
            coverage=100,
            failed_nudges=0,
            issues=[],
        )
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(
            nzbdav_arr_report_validation,
            "fetch_documents",
            return_value=clean_documents,
        ):
            output_dir = pathlib.Path(directory)
            result = nzbdav_arr_report_validation.main([
                "--base-url",
                "https://example.test/nzbdav/protocol/",
                "--api-key",
                "x",
                "--output-dir",
                str(output_dir),
            ])
            artifact_path = next(output_dir.glob("*.json"))
            artifact = json.loads(artifact_path.read_text(encoding="utf-8"))

        self.assertEqual(result, 0)
        self.assertEqual(
            artifact["nzbdav_protocol_base"],
            "https://example.test/nzbdav/protocol",
        )
        self.assertEqual(artifact["base_url"], artifact["nzbdav_protocol_base"])

    def test_ipv6_artifact_preserves_the_normalized_protocol_base_unchanged(self):
        clean_documents = documents(
            sonarr=True,
            radarr=True,
            search_mode="report",
            duplicate_behavior="increment",
            coverage=100,
            failed_nudges=0,
            issues=[],
        )
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(
            nzbdav_arr_report_validation,
            "fetch_documents",
            return_value=clean_documents,
        ):
            output_dir = pathlib.Path(directory)
            result = nzbdav_arr_report_validation.main([
                "--base-url",
                "http://[::1]:3000/protocol/",
                "--api-key",
                "x",
                "--output-dir",
                str(output_dir),
            ])
            artifact_path = next(output_dir.glob("*.json"))
            artifact = json.loads(artifact_path.read_text(encoding="utf-8"))

        self.assertEqual(result, 0)
        self.assertEqual(
            artifact["nzbdav_protocol_base"],
            "http://[::1]:3000/protocol",
        )
        self.assertEqual(artifact["base_url"], artifact["nzbdav_protocol_base"])

    def test_cli_traceback_suppresses_invalid_port_input_and_helper_exception(self):
        configured = "https://example.test:arr-port-trace-canary/protocol"

        with self.assertRaises(SystemExit) as captured:
            nzbdav_arr_report_validation.main([
                "--base-url",
                configured,
                "--api-key",
                "x",
            ])

        rendered = "".join(traceback.format_exception(captured.exception))
        self.assertEqual(str(captured.exception), SAFE_BASE_ERROR)
        self.assertEqual(rendered.count(SAFE_BASE_ERROR), 1)
        self.assertNotIn(configured, rendered)
        self.assertNotIn("arr-port-trace-canary", rendered)
        self.assertNotIn("ValueError", rendered)
        self.assertNotIn("direct cause", rendered)

    def test_validate_documents_reads_non_secret_policy_from_validation(self):
        checks = nzbdav_arr_report_validation.validate_documents(
            {
                "validation": {
                    "configured_apps": ["radarr", "sonarr"],
                    "search_nudge_mode": "report",
                    "duplicate_nzb_behavior": "increment",
                    "queue_items": 1,
                    "correlation_coverage_percent": 95,
                    "search_nudges": {"failed": 0},
                    "issues": [],
                },
                "search_nudges": {"commands": []},
                "fullstatus": {},
            },
            min_correlation=90,
            low_correlation_reason=None,
        )

        self.assertTrue(all(check["passed"] for check in checks))

    def test_validate_documents_accepts_clean_report_mode(self):
        checks = nzbdav_arr_report_validation.validate_documents(
            documents(
                sonarr=True,
                radarr=True,
                search_mode="report",
                duplicate_behavior="increment",
                coverage=95,
                failed_nudges=0,
                issues=[],
            ),
            min_correlation=90,
            low_correlation_reason=None,
        )

        self.assertTrue(all(check["passed"] for check in checks))

    def test_validate_documents_rejects_apply_mode_duplicate_reject_and_failed_nudges(self):
        checks = nzbdav_arr_report_validation.validate_documents(
            documents(
                sonarr=True,
                radarr=True,
                search_mode="apply",
                duplicate_behavior="reject",
                coverage=95,
                failed_nudges=1,
                issues=[{"severity": "error", "code": "bad", "message": "bad"}],
            ),
            min_correlation=90,
            low_correlation_reason=None,
        )

        failed = {check["name"] for check in checks if not check["passed"]}
        self.assertIn("search nudge report mode", failed)
        self.assertIn("duplicate rejection disabled", failed)
        self.assertIn("validation has no errors", failed)
        self.assertIn("no failed search nudges", failed)

    def test_low_correlation_requires_reason(self):
        failed_checks = nzbdav_arr_report_validation.validate_documents(
            documents(
                sonarr=True,
                radarr=True,
                search_mode="report",
                duplicate_behavior="increment",
                coverage=50,
                failed_nudges=0,
                queue_items=10,
                issues=[],
            ),
            min_correlation=90,
            low_correlation_reason=None,
        )
        explained_checks = nzbdav_arr_report_validation.validate_documents(
            documents(
                sonarr=True,
                radarr=True,
                search_mode="report",
                duplicate_behavior="increment",
                coverage=50,
                failed_nudges=0,
                queue_items=10,
                issues=[],
            ),
            min_correlation=90,
            low_correlation_reason="ARR queue contains non-NZBDav legacy jobs.",
        )

        self.assertFalse(next(check for check in failed_checks if check["name"] == "active queue correlation coverage")["passed"])
        self.assertTrue(next(check for check in explained_checks if check["name"] == "active queue correlation coverage")["passed"])

    def test_redact_url_removes_credentials_and_query(self):
        self.assertEqual(
            nzbdav_arr_report_validation.redact_url("https://user:pass@example.com:443/path?apikey=secret"),
            "https://example.com:443/path",
        )

    def test_redact_parses_json_config_strings(self):
        redacted = nzbdav_arr_report_validation.redact({
            "configValue": '{"SonarrInstances":[{"ApiKey":"secret","Host":"http://sonarr:8989"}]}'
        })

        self.assertEqual(redacted["configValue"]["SonarrInstances"][0]["ApiKey"], "***REDACTED***")
        self.assertEqual(redacted["configValue"]["SonarrInstances"][0]["Host"], "http://sonarr:8989")


def documents(
    *,
    sonarr,
    radarr,
    search_mode,
    duplicate_behavior,
    coverage,
    failed_nudges,
    issues,
    queue_items=1,
):
    return {
        "validation": {
            "configured_apps": [
                *(["sonarr"] if sonarr else []),
                *(["radarr"] if radarr else []),
            ],
            "search_nudge_mode": search_mode,
            "duplicate_nzb_behavior": duplicate_behavior,
            "queue_items": queue_items,
            "correlation_coverage_percent": coverage,
            "search_nudges": {"failed": failed_nudges},
            "issues": issues,
        },
        "search_nudges": {"commands": []},
        "fullstatus": {},
    }


if __name__ == "__main__":
    unittest.main()
