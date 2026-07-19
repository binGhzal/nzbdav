import importlib.util
import pathlib
import sys
import traceback
import unittest


TESTS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPTS_DIR = TESTS_DIR.parents[0] / "scripts"
for import_path in (TESTS_DIR, SCRIPTS_DIR):
    if str(import_path) not in sys.path:
        sys.path.insert(0, str(import_path))

from protocol_base_contract_cases import (  # noqa: E402
    INVALID_PROTOCOL_BASES,
    SAFE_BASE_ERROR,
    VALID_PROTOCOL_BASES,
)


HELPER_PATH = SCRIPTS_DIR / "nzbdav_protocol_base.py"


def load_helper():
    if not HELPER_PATH.is_file():
        raise AssertionError("shared NzbDAV protocol-base helper is missing")
    spec = importlib.util.spec_from_file_location("nzbdav_protocol_base", HELPER_PATH)
    if spec is None or spec.loader is None:
        raise AssertionError("shared NzbDAV protocol-base helper cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class ProtocolBaseTests(unittest.TestCase):
    def test_normalizes_root_nested_trailing_and_decoded_protocol_bases(self):
        helper = load_helper()

        for configured, expected in VALID_PROTOCOL_BASES:
            with self.subTest(configured=configured):
                self.assertEqual(
                    helper.normalize_nzbdav_protocol_base(configured),
                    expected,
                )

    def test_rejects_every_ambiguous_or_non_protocol_base_with_one_safe_diagnostic(self):
        helper = load_helper()

        for name, configured in INVALID_PROTOCOL_BASES:
            with self.subTest(name=name):
                with self.assertRaisesRegex(ValueError, rf"^{SAFE_BASE_ERROR}$"):
                    helper.normalize_nzbdav_protocol_base(configured)

    def test_failure_never_echoes_the_rejected_value(self):
        helper = load_helper()
        configured = "https://credential-canary@example.test/protocol?private-canary"

        with self.assertRaises(ValueError) as captured:
            helper.normalize_nzbdav_protocol_base(configured)

        rendered = str(captured.exception)
        self.assertEqual(rendered, SAFE_BASE_ERROR)
        self.assertNotIn("credential-canary", rendered)
        self.assertNotIn("private-canary", rendered)

    def test_invalid_port_traceback_suppresses_rejected_value_and_parser_context(self):
        helper = load_helper()
        configured = "https://example.test:port-trace-canary/protocol"

        with self.assertRaises(ValueError) as captured:
            helper.normalize_nzbdav_protocol_base(configured)

        rendered = "".join(traceback.format_exception(captured.exception))
        self.assertEqual(str(captured.exception), SAFE_BASE_ERROR)
        self.assertEqual(rendered.count(SAFE_BASE_ERROR), 1)
        self.assertNotIn(configured, rendered)
        self.assertNotIn("port-trace-canary", rendered)
        self.assertNotIn("Port could not be cast", rendered)
        self.assertNotIn("During handling of the above exception", rendered)


if __name__ == "__main__":
    unittest.main()
