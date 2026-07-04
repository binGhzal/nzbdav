import importlib.util
import pathlib
import sys
import unittest


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "nzbdav_arr_report_validation.py"
SPEC = importlib.util.spec_from_file_location("nzbdav_arr_report_validation", SCRIPT_PATH)
nzbdav_arr_report_validation = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = nzbdav_arr_report_validation
SPEC.loader.exec_module(nzbdav_arr_report_validation)


class ArrReportValidationTests(unittest.TestCase):
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
    arr_config = {
        "SonarrInstances": [{}] if sonarr else [],
        "RadarrInstances": [{}] if radarr else [],
        "SearchNudge": {"Mode": search_mode},
    }
    return {
        "validation": {
            "queue_items": queue_items,
            "correlation_coverage_percent": coverage,
            "search_nudges": {"failed": failed_nudges},
            "issues": issues,
        },
        "search_nudges": {"commands": []},
        "fullstatus": {"status": {"arr_search_nudge": {"mode": search_mode}}},
        "config": {
            "configItems": [
                {"configName": "arr.instances", "configValue": __import__("json").dumps(arr_config)},
                {"configName": "api.duplicate-nzb-behavior", "configValue": duplicate_behavior},
            ]
        },
    }


if __name__ == "__main__":
    unittest.main()
