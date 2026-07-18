import json
import pathlib
import re
import unittest


REPOSITORY_ROOT = pathlib.Path(__file__).resolve().parents[1]
NODE_IMAGE = (
    "node:24-alpine3.23@"
    "sha256:595398b0081eacda8e1c4c5b97b76cd1020e4d58a8ebcb4843b9bca1e79e7436"
)
DOTNET_SDK_IMAGE = (
    "mcr.microsoft.com/dotnet/sdk:10.0-alpine@"
    "sha256:940f919ae84dd92ccd4aab7686fa5b777870b006c9360351039e16bcaad73d89"
)
DOTNET_ASPNET_IMAGE = (
    "mcr.microsoft.com/dotnet/aspnet:10.0-alpine@"
    "sha256:57bd717ac18ff6c8a39cc0ee4a76c1f15adc46df50434c73eff0c3f1df4c88f0"
)


class RuntimeReleaseContractTests(unittest.TestCase):
    def test_combined_image_pins_compatible_multi_arch_runtimes(self):
        dockerfile = (REPOSITORY_ROOT / "Dockerfile").read_text(encoding="utf-8")

        self.assertIn(f"FROM --platform=$BUILDPLATFORM {NODE_IMAGE} AS frontend-build", dockerfile)
        self.assertIn(f"FROM {NODE_IMAGE} AS node-runtime", dockerfile)
        self.assertIn(f"FROM --platform=$BUILDPLATFORM {DOTNET_SDK_IMAGE} AS backend-build", dockerfile)
        self.assertIn(f"FROM {DOTNET_ASPNET_IMAGE}", dockerfile)
        self.assertIn("COPY --from=node-runtime /usr/local/ /usr/local/", dockerfile)
        self.assertNotRegex(dockerfile, r"apk add[^\n]*(?:nodejs|npm)")

        image_references = [
            line.split()[1 if not line.startswith("FROM --platform=") else 2]
            for line in dockerfile.splitlines()
            if line.startswith("FROM ")
        ]
        self.assertTrue(image_references)
        self.assertTrue(all("@sha256:" in image for image in image_references))

    def test_frontend_image_uses_the_same_pinned_node_lts(self):
        dockerfile = (REPOSITORY_ROOT / "frontend" / "Dockerfile").read_text(encoding="utf-8")
        from_lines = [line for line in dockerfile.splitlines() if line.startswith("FROM ")]

        self.assertEqual(
            [f"FROM {NODE_IMAGE} AS build-env", f"FROM {NODE_IMAGE}"],
            from_lines,
        )

    def test_frontend_package_and_lock_target_node_24_only(self):
        package = json.loads(
            (REPOSITORY_ROOT / "frontend" / "package.json").read_text(encoding="utf-8")
        )
        package_lock = json.loads(
            (REPOSITORY_ROOT / "frontend" / "package-lock.json").read_text(encoding="utf-8")
        )

        self.assertEqual(">=24 <25", package["engines"]["node"])
        self.assertEqual(">=11 <12", package["engines"]["npm"])
        self.assertRegex(package["devDependencies"]["@types/node"], r"^\^24(?:\.|$)")
        self.assertEqual("24", (REPOSITORY_ROOT / ".nvmrc").read_text(encoding="utf-8").strip())

        locked_root = package_lock["packages"][""]
        self.assertEqual(package["engines"], locked_root["engines"])
        self.assertRegex(locked_root["devDependencies"]["@types/node"], r"^\^24(?:\.|$)")
        locked_node_types = package_lock["packages"]["node_modules/@types/node"]["version"]
        self.assertRegex(locked_node_types, r"^24\.")

    def test_frontend_dependency_install_scripts_are_explicitly_denied_and_fail_closed(self):
        package = json.loads(
            (REPOSITORY_ROOT / "frontend" / "package.json").read_text(encoding="utf-8")
        )
        npmrc = (REPOSITORY_ROOT / "frontend" / ".npmrc").read_text(
            encoding="utf-8"
        )

        self.assertEqual(
            {
                "@parcel/watcher": False,
                "esbuild": False,
                "fsevents@2.3.2": True,
                "fsevents@2.3.3": True,
            },
            package["allowScripts"],
        )
        self.assertRegex(npmrc, r"(?m)^strict-allow-scripts=true$")

    def test_frontend_has_no_unused_deprecated_css_type_generator(self):
        package = json.loads(
            (REPOSITORY_ROOT / "frontend" / "package.json").read_text(encoding="utf-8")
        )
        package_lock = json.loads(
            (REPOSITORY_ROOT / "frontend" / "package-lock.json").read_text(encoding="utf-8")
        )

        self.assertNotIn("typed-css-modules", package["devDependencies"])
        self.assertNotIn("node_modules/typed-css-modules", package_lock["packages"])

    def test_dockerfiles_have_no_floating_runtime_from_reference(self):
        for relative_path in ("Dockerfile", "frontend/Dockerfile"):
            dockerfile = (REPOSITORY_ROOT / relative_path).read_text(encoding="utf-8")
            with self.subTest(dockerfile=relative_path):
                for line in dockerfile.splitlines():
                    if not line.startswith("FROM "):
                        continue
                    match = re.match(r"FROM (?:--platform=\S+ )?(\S+)", line)
                    self.assertIsNotNone(match)
                    self.assertIn("@sha256:", match.group(1))


if __name__ == "__main__":
    unittest.main()
