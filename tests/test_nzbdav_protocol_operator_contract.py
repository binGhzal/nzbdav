from __future__ import annotations

import pathlib
import re
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
OPERATOR_FILES = (
    ROOT / "README.md",
    ROOT / ".env.example",
    ROOT / "docs" / "setup-guide.md",
    ROOT / "docs" / "url-base.md",
    ROOT / "docs" / "dfs-rclone-benchmark.md",
    ROOT / "docs" / "grab-to-plex-benchmark.md",
    ROOT / "examples" / "nginx" / "README.md",
    ROOT / "examples" / "nginx" / "subdomain.conf",
    ROOT / "examples" / "nginx" / "subfolder.conf",
    ROOT / "frontend" / "README.md",
)
SUBDOMAIN_CONFIG = ROOT / "examples" / "nginx" / "subdomain.conf"
SUBFOLDER_CONFIG = ROOT / "examples" / "nginx" / "subfolder.conf"


class ProtocolOperatorContractTests(unittest.TestCase):
    def test_operator_surfaces_publish_root_and_nested_protocol_bases(self):
        expected = {
            ROOT / "README.md": (
                "https://nzbdav.example.com/protocol",
                "https://example.com/nzbdav/protocol",
            ),
            ROOT / ".env.example": (
                "NZBDAV_BASE_URL=https://nzbdav.example.com/protocol",
            ),
            ROOT / "docs" / "setup-guide.md": (
                "http://nzbdav:3000/protocol",
                "https://nzbdav.example.com/protocol",
            ),
            ROOT / "docs" / "url-base.md": (
                "https://nzbdav.example.com/protocol",
                "https://example.com/nzbdav/protocol",
            ),
            ROOT / "docs" / "dfs-rclone-benchmark.md": (
                "http://localhost:3000/protocol",
            ),
            ROOT / "docs" / "grab-to-plex-benchmark.md": (
                "http://nzbdav:3000/protocol",
                "https://example.com/nzbdav/protocol",
            ),
            ROOT / "examples" / "nginx" / "README.md": (
                "https://nzbdav.example.com/protocol",
                "https://example.com/nzbdav/protocol",
            ),
        }

        for path, required_values in expected.items():
            document = read(path)
            for required in required_values:
                with self.subTest(path=path.relative_to(ROOT), required=required):
                    self.assertIn(required, document)

    def test_rclone_remotes_mount_the_full_protocol_root(self):
        setup = markdown_section(
            read(ROOT / "docs" / "setup-guide.md"),
            "### 2. Generate Rclone Config",
        )
        setup_remote = fenced_block_containing(setup, "[nzbdav]")
        self.assertIn("url = http://nzbdav:3000/protocol\n", setup_remote)
        self.assertNotIn("/protocol/content", setup_remote)

        url_base = markdown_section(
            read(ROOT / "docs" / "url-base.md"),
            "### rclone (WebDAV)",
        )
        url_base_remote = fenced_block_containing(url_base, "[nzbdav]")
        self.assertIn("url = https://example.com/nzbdav/protocol", url_base_remote)
        self.assertIn("https://nzbdav.example.com/protocol", url_base_remote)
        self.assertNotIn("/protocol/content", url_base_remote)
        for child in (".ids", "completed-symlinks", "content", "nzbs"):
            with self.subTest(section="url-base rclone", child=child):
                self.assertIn(child, url_base)

        nginx_rclone = markdown_section(
            read(ROOT / "examples" / "nginx" / "README.md"),
            "## rclone (WebDAV) settings",
        )
        self.assertIn("`https://example.com/nzbdav/protocol`", nginx_rclone)
        self.assertIn("`https://nzbdav.example.com/protocol`", nginx_rclone)
        self.assertNotIn("/protocol/content", nginx_rclone)
        for child in (".ids", "completed-symlinks", "content", "nzbs"):
            with self.subTest(section="nginx rclone", child=child):
                self.assertIn(child, nginx_rclone)

        mount_check = markdown_section(
            read(ROOT / "docs" / "setup-guide.md"),
            "### 3. Update `docker-compose.yml`",
        )
        self.assertIn("# Should show: .ids, completed-symlinks, content, nzbs", mount_check)
        healthcheck = fenced_block_containing(mount_check, "test -d /mnt/remote/nzbdav/.ids")
        self.assertIn("test -d /mnt/remote/nzbdav/content", healthcheck)

    def test_arr_download_client_section_binds_all_arrs_to_protocol_url_base(self):
        setup = read(ROOT / "docs" / "setup-guide.md")
        download_client = markdown_section(
            setup,
            "### 1. Add NzbDav Download Client to Radarr/Sonarr/Lidarr",
        )

        for application in ("Sonarr", "Radarr", "Lidarr"):
            with self.subTest(application=application):
                self.assertIn(application, download_client)
        self.assertIn("* URL Base: `/protocol`", download_client)
        self.assertIn("* Nested URL Base: `/nzbdav/protocol`", download_client)

        arr_instances = markdown_section(
            setup,
            "### 2. Configure NzbDav for Radarr/Sonarr/Lidarr",
        )
        self.assertIn("**Lidarr Instances > Add**", arr_instances)
        self.assertIn("http://lidarr:8686", arr_instances)
        self.assertIn("correlation/report-only", arr_instances)

    def test_readme_direct_http_runs_use_checked_nonprinting_local_auth(self):
        getting_started = markdown_section(read(ROOT / "README.md"), "# Getting Started")
        self.assert_checked_nonprinting_session_key_recipe(getting_started)
        docker_runs = fenced_blocks_containing(getting_started, "docker run")
        self.assertEqual(len(docker_runs), 2)
        for block in docker_runs:
            with self.subTest(block=block):
                self.assert_session_recipe_precedes(getting_started, block)
                self.assert_local_http_container_contract(block)
                self.assertIn("-p 127.0.0.1:3000:3000", block)

    def test_setup_first_run_compose_requires_checked_nonprinting_local_auth(self):
        initial_deployment = markdown_section(
            read(ROOT / "docs" / "setup-guide.md"),
            "## Phase 2: Initial Deployment",
        )
        self.assert_checked_nonprinting_session_key_recipe(initial_deployment)
        compose = fenced_block_containing(initial_deployment, "container_name: nzbdav")
        self.assert_session_recipe_precedes(initial_deployment, compose)
        self.assert_local_http_compose_contract(compose)
        start = fenced_block_containing(initial_deployment, "docker compose up -d")
        self.assertIn("export SESSION_KEY", initial_deployment[:initial_deployment.find(start)])

    def test_contributor_container_examples_require_checked_nonprinting_local_auth(self):
        contributing = read(ROOT / "CONTRIBUTING.md")
        docker_section = markdown_section(contributing, "## Build Docker image")
        self.assert_checked_nonprinting_session_key_recipe(contributing)
        docker_run = fenced_block_containing(docker_section, "docker run --rm -it")
        self.assert_session_recipe_precedes(contributing, docker_run)
        self.assert_local_http_container_contract(docker_run)
        self.assertIn("-p 127.0.0.1:3333:3000", docker_run)
        compose = fenced_block_containing(docker_section, "services:\n  nzbdav:")
        self.assert_session_recipe_precedes(contributing, compose)
        self.assert_local_http_compose_contract(compose)

    def test_contributor_split_development_declares_local_http_auth(self):
        contributing = read(ROOT / "CONTRIBUTING.md")
        setup = markdown_section(contributing, "## Set up your system")
        self.assert_checked_nonprinting_session_key_recipe(setup)
        self.assertIn("export AUTH_MODE=local", setup)
        self.assertIn("export SECURE_COOKIES=false", setup)
        self.assertIn("export ALLOW_INSECURE_COOKIES=true", setup)
        self.assertIn("configure_dev_internal_api_key || exit 1", setup)
        self.assert_session_recipe_precedes(contributing, "npm run dev")
        self.assertIn("npm run dev", contributing)

    def test_https_proxy_examples_require_persistent_secure_local_auth(self):
        url_base = read(ROOT / "docs" / "url-base.md")
        compose_section = markdown_section(url_base, "### Docker Compose example")
        self.assert_checked_nonprinting_session_key_recipe(compose_section)
        compose = fenced_block_containing(compose_section, "URL_BASE: /nzbdav")
        self.assert_session_recipe_precedes(url_base, compose)
        self.assert_secure_https_compose_contract(compose)

        docker_section = markdown_section(url_base, "### Plain Docker example")
        docker_run = fenced_block_containing(docker_section, "docker run")
        self.assert_session_recipe_precedes(url_base, docker_run)
        self.assertIn("-p 127.0.0.1:3000:3000", docker_run)
        self.assert_local_http_container_contract(docker_run)

        nginx_setup = markdown_section(
            read(ROOT / "examples" / "nginx" / "README.md"),
            "### Subfolder setup",
        )
        self.assert_checked_nonprinting_session_key_recipe(nginx_setup)
        nginx_compose = fenced_block_containing(nginx_setup, "URL_BASE: /nzbdav")
        self.assert_session_recipe_precedes(nginx_setup, nginx_compose)
        self.assert_secure_https_compose_contract(nginx_compose)

    def test_env_example_does_not_tell_operators_to_print_session_key_material(self):
        environment = read(ROOT / ".env.example")
        self.assertNotIn("Generate one with: openssl rand -hex 32", environment)
        self.assertIn("capture", environment)
        self.assertIn("without printing", environment)

    def test_frontend_readme_rejects_standalone_v1_deployment(self):
        frontend_readme = read(ROOT / "frontend" / "README.md")
        self.assertIn("standalone frontend image is not a supported V1 deployment", frontend_readme)
        self.assertIn("../CONTRIBUTING.md", frontend_readme)
        self.assertNotIn("docker build -t my-app", frontend_readme)
        self.assertNotIn("docker run -p 3000:3000 my-app", frontend_readme)

    def test_aiostreams_service_uses_exact_protocol_bases(self):
        setup = read(ROOT / "docs" / "setup-guide.md")
        aiostreams = markdown_section(setup, "### 1. Configure NzbDav Service")
        self.assertIn(
            "**NzbDAV URL:** `http://nzbdav:3000/protocol`",
            aiostreams,
        )
        self.assertIn(
            "`https://example.com/nzbdav/protocol`",
            aiostreams,
        )
        self.assertNotIn("http://nzbdav:3000`", aiostreams)

    def test_dfs_benchmark_rclone_baseline_matches_setup_command(self):
        setup = markdown_section(
            read(ROOT / "docs" / "setup-guide.md"),
            "### 3. Update `docker-compose.yml`",
        )
        setup_command = fenced_block_containing(setup, "--vfs-cache-mode=full")
        benchmark = markdown_section(
            read(ROOT / "docs" / "dfs-rclone-benchmark.md"),
            "## Baseline rclone scenario",
        )
        required = (
            "--links",
            "--use-cookies",
            "--allow-other",
            "--rc",
            "--vfs-cache-mode=full",
            "--vfs-cache-max-size=20G",
            "--vfs-cache-max-age=6h",
            "--vfs-cache-poll-interval=1m",
            "--buffer-size=0M",
            "--vfs-read-ahead=512M",
            "--vfs-read-chunk-size=4M",
            "--vfs-read-chunk-streams=16",
            "--dir-cache-time=20s",
        )
        for flag in required:
            with self.subTest(flag=flag):
                self.assertIn(flag, setup_command)
                self.assertIn(flag, benchmark)
        self.assertRegex(setup_command, r"(?m)^\s*--rc\s*$")
        self.assertRegex(benchmark, r"(?m)^\* `--rc`(?:\.| with )")
        for authenticated_rc in (
            "--rc-addr=:5572",
            "--rc-user=nzbdav",
            "--rc-pass=replace-with-long-random-password",
        ):
            with self.subTest(authenticated_rc=authenticated_rc):
                self.assertRegex(
                    setup_command,
                    rf"(?m)^\s*{re.escape(authenticated_rc)}\s*$",
                )
        for stale in (
            "--vfs-cache-mode=writes",
            "--vfs-read-ahead=0",
            "--vfs-read-chunk-size=256K",
            "--vfs-read-chunk-size-limit=8M",
            "--vfs-read-chunk-streams=0",
            "--vfs-cache-poll-interval=30s",
        ):
            with self.subTest(stale=stale):
                self.assertNotIn(stale, benchmark)
        self.assertNotIn("/opt/media-stack", read(ROOT / "docs" / "dfs-rclone-benchmark.md"))

    def test_public_setup_is_v1_sqlite_only_without_postgresql_internals(self):
        setup = read(ROOT / "docs" / "setup-guide.md")
        database = markdown_section(setup, "**D. Database Settings**")
        self.assertIn("SQLite is the only supported V1 runtime", database)
        self.assertIn("NZBDAV_DATABASE_PROVIDER=postgres", database)
        self.assertIn("reject", database.lower())
        for private_detail in (
            "PostgreSQL may be enabled",
            "docs/superpowers/",
            "READ COMMITTED",
            "lock_timeout",
            "schema-qualified",
            "provider-native schema",
            "whole-snapshot JSON",
        ):
            with self.subTest(private_detail=private_detail):
                self.assertNotIn(private_detail, setup)

    def test_operator_surfaces_do_not_publish_legacy_ingress_or_protocol_view(self):
        combined = "\n".join(read(path) for path in OPERATOR_FILES)
        forbidden_literals = (
            "NZBDAV_BASE_URL=https://nzbdav.example.com\n",
            "NZBDAV_BENCH_BASE_URL=http://localhost:3000/ ",
            "--base-url http://localhost:3000/ ",
            "NZBDAV_E2E_NZBDAV_URL=http://nzbdav:3000\n",
            "url = http://nzbdav:3000/\n",
            "https://example.com/nzbdav/api",
            "https://example.com/nzbdav/content",
            "https://nzbdav.example.com/content",
            "http://localhost:8080/view",
            "/protocol/view",
        )

        for forbidden in forbidden_literals:
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, combined)

        external_legacy = re.compile(
            r"https?://[^\s`'\"]+/(?:api|nzbs|content|\.ids|completed-symlinks)(?:[/?!#]|$)",
            re.IGNORECASE,
        )
        matches = [match.group(0) for match in external_legacy.finditer(combined)]
        self.assertEqual(matches, [])

    def test_setup_preserves_report_mode_and_keeps_view_principal_only(self):
        setup = read(ROOT / "docs" / "setup-guide.md")
        url_base = read(ROOT / "docs" / "url-base.md")

        self.assertIn("SearchNudge.Enabled=false", read(ROOT / ".env.example"))
        self.assertIn("SearchNudge.Mode=report", read(ROOT / ".env.example"))
        self.assertIn("Keep search nudging in `report` mode first", setup)
        self.assertIn("frontend-principal", url_base)
        self.assertRegex(url_base, r"(?i)/view[^\n]{0,160}(?:principal|authenticated)")
        self.assertNotRegex(url_base, r"(?i)(?:auth_basic|auth_request)\s+off[^\n]{0,160}/view")

    def test_benchmark_docs_distinguish_http_suffixes_from_filesystem_paths(self):
        benchmark = read(ROOT / "docs" / "dfs-rclone-benchmark.md")

        self.assertIn("logical suffixes below the NzbDAV protocol base", benchmark)
        self.assertNotIn("WebDAV paths, or absolute URLs", benchmark)

    def test_nginx_has_one_path_preserving_exact_protocol_auth_bypass(self):
        cases = (
            (
                SUBDOMAIN_CONFIG,
                "location ~ ^/protocol(?:/|$) {",
                "location / {",
            ),
            (
                SUBFOLDER_CONFIG,
                "location ~ ^/nzbdav/protocol(?:/|$) {",
                "location /nzbdav/ {",
            ),
        )

        for path, protocol_header, ui_header in cases:
            document = read(path)
            with self.subTest(path=path.name):
                self.assertEqual(document.count(protocol_header), 1)
                self.assertEqual(document.count("auth_basic   off;"), 1)
                self.assertEqual(document.count("auth_request off;"), 1)
                protocol = location_block(document, protocol_header)
                ui = location_block(document, ui_header)

                self.assertIn("auth_basic   off;", protocol)
                self.assertIn("auth_request off;", protocol)
                self.assertIn("proxy_pass http://nzbdav;", protocol)
                self.assertIn("proxy_buffering    off;", protocol)
                self.assertIn("proxy_request_buffering off;", protocol)
                self.assertIn("proxy_read_timeout 6h;", protocol)
                self.assertIn("proxy_send_timeout 6h;", protocol)
                self.assertIn("client_max_body_size 0;", protocol)
                self.assertNotIn("Upgrade", protocol)
                self.assertNotIn("Connection", protocol)
                self.assertNotRegex(
                    protocol,
                    r"(?i)(?:^|[/|(])(?:api|nzbs|content|\.ids|completed-symlinks|view)(?:[/|)$]|$)",
                )

                self.assertIn("proxy_set_header Upgrade", ui)
                self.assertIn("proxy_set_header Connection", ui)
                self.assertNotIn("auth_basic   off;", ui)
                self.assertNotIn("auth_request off;", ui)

        self.assertNotIn("location ^~ /nzbdav/", read(SUBFOLDER_CONFIG))

    def assert_checked_nonprinting_session_key_recipe(self, document: str):
        self.assertIn("configure_nzbdav_session_key()", document)
        self.assertIn("hexdump -n 32", document)
        self.assertIn("${#nzbdav_session_key_candidate}", document)
        self.assertIn("*[!0-9a-f]*", document)
        self.assertIn('export SESSION_KEY="$nzbdav_session_key_candidate"', document)
        self.assertIn("unset nzbdav_session_key_candidate", document)
        self.assertIn("configure_nzbdav_session_key || exit 1", document)
        self.assertNotRegex(
            document,
            r"(?m)^(?:echo|printf)[^\n]*\$nzbdav_session_key_candidate",
        )

    def assert_session_recipe_precedes(self, document: str, dependent: str):
        invocation = document.find("configure_nzbdav_session_key || exit 1")
        dependency = document.find(dependent)
        self.assertGreaterEqual(invocation, 0)
        self.assertGreater(dependency, invocation)

    def assert_local_http_container_contract(self, block: str):
        self.assertIn("-e AUTH_MODE=local", block)
        self.assertRegex(block, r"(?m)^\s*-e SESSION_KEY \\\s*$")
        self.assertNotRegex(block, r"-e SESSION_KEY=")
        self.assertIn("-e SECURE_COOKIES=false", block)
        self.assertIn("-e ALLOW_INSECURE_COOKIES=true", block)

    def assert_local_http_compose_contract(self, block: str):
        self.assertIn("AUTH_MODE=local", block)
        self.assertIn("SESSION_KEY=${SESSION_KEY:?", block)
        self.assertIn("SECURE_COOKIES=false", block)
        self.assertIn("ALLOW_INSECURE_COOKIES=true", block)

    def assert_secure_https_compose_contract(self, block: str):
        self.assertIn("AUTH_MODE: local", block)
        self.assertIn("SESSION_KEY: ${SESSION_KEY:?", block)
        self.assertIn('SECURE_COOKIES: "true"', block)
        self.assertNotIn("ALLOW_INSECURE_COOKIES", block)


def read(path: pathlib.Path) -> str:
    return path.read_text(encoding="utf-8")


def location_block(document: str, header: str) -> str:
    start = document.find(header)
    if start < 0:
        raise AssertionError(f"missing Nginx location: {header}")
    depth = 0
    for index in range(start, len(document)):
        character = document[index]
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return document[start:index + 1]
    raise AssertionError(f"unterminated Nginx location: {header}")


def markdown_section(document: str, heading: str) -> str:
    start = document.find(heading)
    if start < 0:
        raise AssertionError(f"missing Markdown heading: {heading}")
    line = heading.splitlines()[0]
    if line.startswith("#"):
        level = len(line) - len(line.lstrip("#"))
        position = document.find("\n", start) + 1
        in_fence = False
        for current_line in document[position:].splitlines(keepends=True):
            if current_line.startswith("```"):
                in_fence = not in_fence
            elif not in_fence and re.match(rf"^#{{1,{level}}}\s", current_line):
                return document[start:position]
            position += len(current_line)
        return document[start:]

    next_bold = re.compile(r"^\*\*[A-Z]\.\s", re.MULTILINE)
    match = next_bold.search(document, start + len(line))
    end = match.start() if match else len(document)
    return document[start:end]


def fenced_blocks(document: str) -> list[str]:
    return re.findall(r"```[^\n]*\n(.*?)\n```", document, re.DOTALL)


def fenced_blocks_containing(document: str, needle: str) -> list[str]:
    return [block for block in fenced_blocks(document) if needle in block]


def fenced_block_containing(document: str, needle: str) -> str:
    matches = fenced_blocks_containing(document, needle)
    if len(matches) != 1:
        raise AssertionError(
            f"expected one fenced block containing {needle!r}, found {len(matches)}"
        )
    return matches[0]


if __name__ == "__main__":
    unittest.main()
