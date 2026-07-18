import pathlib
import re
import unittest


REPOSITORY_ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKFLOW_ROOT = REPOSITORY_ROOT / ".github" / "workflows"
VERIFY_WORKFLOW = WORKFLOW_ROOT / "verify.yml"
CALLER_WORKFLOWS = (
    "ci.yml",
    "branch.yml",
    "dependabot.yml",
    "pre-release.yml",
    "ghcr-release.yml",
)
NON_PUBLISHING_WORKFLOWS = (
    "branch.yml",
    "dependabot.yml",
    "pre-release.yml",
    "ghcr-release.yml",
)
POSTGRES_IMAGE = (
    "postgres:16.14-alpine@"
    "sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777"
)
DOTNET_SDK_IMAGE = (
    "mcr.microsoft.com/dotnet/sdk:10.0-alpine@"
    "sha256:940f919ae84dd92ccd4aab7686fa5b777870b006c9360351039e16bcaad73d89"
)
NATIVE_TRANSFER_FILTER = (
    "FullyQualifiedName~backend.Tests.Database.Transfer&"
    "FullyQualifiedName!~backend.Tests.Database.Transfer."
    "TransferV3ImportStateStorePostgreSqlTests"
)


class ReleaseWorkflowContractTests(unittest.TestCase):
    def test_external_actions_are_pinned_and_checkouts_drop_credentials(self):
        for workflow_path in WORKFLOW_ROOT.glob("*.yml"):
            workflow = workflow_path.read_text(encoding="utf-8")
            with self.subTest(workflow=workflow_path.name):
                external_uses = re.findall(r"(?m)^\s*uses:\s*([^\s#]+)", workflow)
                for action in external_uses:
                    if action.startswith("./"):
                        continue
                    self.assertRegex(action, r"^[^@]+@[0-9a-f]{40}$")

                checkout_steps = re.findall(
                    r"(?ms)^\s*- name: Checkout repository\n"
                    r"(?P<body>.*?)(?=^\s*- name:|^\S|\Z)",
                    workflow,
                )
                for checkout_step in checkout_steps:
                    self.assertIn("persist-credentials: false", checkout_step)

    def test_publish_workflows_have_least_privilege_defaults(self):
        for workflow_name in NON_PUBLISHING_WORKFLOWS:
            workflow = (WORKFLOW_ROOT / workflow_name).read_text(encoding="utf-8")
            with self.subTest(workflow=workflow_name):
                self.assertRegex(
                    workflow,
                    r"(?m)^permissions:\n  contents: read$",
                )

    def test_shell_steps_do_not_interpolate_untrusted_event_values(self):
        direct_shell_expressions = (
            '${{ github.ref_name }}',
            '${{ github.event.inputs.version }}',
            '${{ github.event.inputs.publish_latest }}',
            '${{ github.repository }}',
        )
        for workflow_name in NON_PUBLISHING_WORKFLOWS:
            workflow = (WORKFLOW_ROOT / workflow_name).read_text(encoding="utf-8")
            run_blocks = re.findall(
                r"(?ms)^\s+run:\s*[|>]?-?\n(?P<body>.*?)(?=^\s+- name:|^\s{0,6}\S|\Z)",
                workflow,
            )
            with self.subTest(workflow=workflow_name):
                for run_block in run_blocks:
                    for expression in direct_shell_expressions:
                        self.assertNotIn(expression, run_block)

    def test_package_publication_is_disabled_while_v1_is_no_go(self):
        forbidden_fragments = (
            "packages: write",
            "docker/login-action",
            "docker/build-push-action",
            "push: true",
        )
        for workflow_name in NON_PUBLISHING_WORKFLOWS:
            workflow = (WORKFLOW_ROOT / workflow_name).read_text(encoding="utf-8")
            with self.subTest(workflow=workflow_name):
                for fragment in forbidden_fragments:
                    self.assertNotIn(fragment, workflow)

    def test_every_ci_and_publish_path_uses_one_reusable_verification_gate(self):
        self.assertTrue(VERIFY_WORKFLOW.is_file())
        for workflow_name in CALLER_WORKFLOWS:
            workflow = (WORKFLOW_ROOT / workflow_name).read_text(encoding="utf-8")
            with self.subTest(workflow=workflow_name):
                self.assertRegex(
                    workflow,
                    r"(?m)^  verify:\n(?:    .*\n)*?    uses: \./\.github/workflows/verify\.yml$",
                )
                self.assertNotIn("node-version:", workflow)
                self.assertNotIn("dotnet test ", workflow)
                self.assertNotIn("npm run test", workflow)

    def test_reusable_gate_covers_all_release_blockers(self):
        workflow = VERIFY_WORKFLOW.read_text(encoding="utf-8")

        required_fragments = (
            "workflow_call:",
            "fetch-depth: 0",
            POSTGRES_IMAGE,
            'node-version: "24.x"',
            "NZBDAV_REQUIRE_POSTGRES_TESTS: \"1\"",
            "postgres-first.trx",
            "backend-full.trx",
            "validate_trx_results.py",
            "python3 -m unittest discover -s tests -p 'test_*.py'",
            "python3 -m compileall -q scripts tests",
            "npm audit --audit-level=moderate",
            "npm run typecheck",
            "npm run test",
            "npm run build",
            "npm run build:server",
            "npx playwright install --with-deps chromium",
            "npm run test:e2e",
            "docker build -t nzbdav:ci .",
            "sh tests/test_entrypoint_contract.sh",
            "sh tests/test_entrypoint_container.sh",
            "-warnaserror:NU1901,NU1902,NU1903,NU1904",
            "Assert pinned runtime versions",
            "Test migration failure exits nonzero",
            "Assert public PostgreSQL migration command remains disabled",
            "Validate changed-range whitespace",
            "github.event.pull_request.base.sha",
            "github.event.before",
        )
        for fragment in required_fragments:
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, workflow)

        self.assertIn(DOTNET_SDK_IMAGE, workflow)
        self.assertIn("--network host", workflow)
        self.assertIn("FullyQualifiedName~PostgreSql", workflow)
        self.assertNotRegex(workflow, r"(?m)^\s*run: git diff --check\s*$")

    def test_trx_validator_fails_closed_on_skips_and_empty_selection(self):
        validator = (REPOSITORY_ROOT / "scripts" / "validate_trx_results.py").read_text(
            encoding="utf-8"
        )

        self.assertIn('values.get("total", 0)', validator)
        self.assertIn('values.get("executed", 0)', validator)
        self.assertIn('values.get("notExecuted", 0)', validator)
        self.assertRegex(validator, r"total\s*<=\s*0")
        self.assertRegex(validator, r"executed\s*!=\s*total")
        self.assertRegex(validator, r"not_executed\s*!=\s*0")

    def test_native_transfer_matrix_excludes_dedicated_postgresql_tests(self):
        workflow = VERIFY_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn(f'--filter "{NATIVE_TRANSFER_FILTER}"', workflow)
        self.assertNotIn(
            '--filter "FullyQualifiedName~backend.Tests.Database.Transfer"',
            workflow,
        )

    def test_dependabot_tracks_both_pinned_dockerfiles(self):
        dependabot = (REPOSITORY_ROOT / ".github" / "dependabot.yml").read_text(
            encoding="utf-8"
        )

        docker_entries = re.findall(
            r'package-ecosystem:\s*"docker"\s*\n\s*directory:\s*"([^"]+)"',
            dependabot,
        )
        self.assertEqual({"/", "/frontend"}, set(docker_entries))


if __name__ == "__main__":
    unittest.main()
