import importlib.util
import pathlib
import sys
import unittest
from unittest import mock


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_arr_report_validation.py"
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
            documents = nzbdav_arr_report_validation.fetch_documents("https://nzbdav.example", "public-key")

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
