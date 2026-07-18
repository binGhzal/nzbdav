import importlib.util
import pathlib
import tempfile
import unittest


SCRIPT_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "validate_trx_results.py"
SPEC = importlib.util.spec_from_file_location("validate_trx_results", SCRIPT_PATH)
validate_trx_results = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(validate_trx_results)


class ValidateTrxResultsTests(unittest.TestCase):
    def test_accepts_nonempty_fully_executed_successful_result(self):
        path = self.write_trx(total=2, executed=2, passed=2, notExecuted=0)

        counters = validate_trx_results.validate_trx(path)

        self.assertEqual(2, counters.total)
        self.assertEqual(2, counters.executed)
        self.assertEqual(0, counters.not_executed)

    def test_rejects_empty_selection(self):
        path = self.write_trx(total=0, executed=0, passed=0, notExecuted=0)

        with self.assertRaisesRegex(ValueError, "selected no tests"):
            validate_trx_results.validate_trx(path)

    def test_rejects_not_executed_tests(self):
        path = self.write_trx(total=2, executed=1, passed=1, notExecuted=1)

        with self.assertRaisesRegex(ValueError, "notExecuted=1"):
            validate_trx_results.validate_trx(path)

    def test_rejects_failed_like_outcomes(self):
        path = self.write_trx(total=2, executed=2, passed=1, failed=1, notExecuted=0)

        with self.assertRaisesRegex(ValueError, "failedLike=1"):
            validate_trx_results.validate_trx(path)

    def test_rejects_missing_counters(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "missing.trx"
            path.write_text("<TestRun />", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "missing TRX counters"):
                validate_trx_results.validate_trx(path)

    def write_trx(self, **attributes):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = pathlib.Path(directory.name) / "result.trx"
        encoded_attributes = " ".join(
            f'{key}="{value}"' for key, value in attributes.items()
        )
        path.write_text(
            f'<TestRun xmlns="urn:test"><ResultSummary><Counters {encoded_attributes} />'
            "</ResultSummary></TestRun>",
            encoding="utf-8",
        )
        return path


if __name__ == "__main__":
    unittest.main()
