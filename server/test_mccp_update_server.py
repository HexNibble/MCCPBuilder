import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import mccp_update_server as server


class UpdateServerTests(unittest.TestCase):
    @staticmethod
    def create_release_archive(
            archive_path: Path,
            product_id: str,
            release_id: str,
            version: str,
            payload: bytes = b"payload") -> None:
        relative = ".minecraft/mods/example.jar"
        manifest = {
            "schemaVersion": "1.0",
            "productId": product_id,
            "releaseId": release_id,
            "version": version,
            "publishedAt": "2026-07-30T00:00:00+00:00",
            "files": [
                {
                    "path": relative,
                    "size": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest().upper(),
                    "preserveExisting": False,
                }
            ],
        }
        with zipfile.ZipFile(
                archive_path,
                mode="w",
                compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr(
                "manifest.json",
                json.dumps(manifest).encode("utf-8"))
            archive.writestr("payload/" + relative, payload)

    def test_parse_range_supports_normal_open_and_suffix_ranges(self):
        self.assertEqual(
            (10, 19),
            server.Handler.parse_range("bytes=10-19", 100))
        self.assertEqual(
            (90, 99),
            server.Handler.parse_range("bytes=-10", 100))
        self.assertEqual(
            (95, 99),
            server.Handler.parse_range("bytes=95-", 100))
        with self.assertRaises(ValueError):
            server.Handler.parse_range("bytes=100-101", 100)
        with self.assertRaises(ValueError):
            server.Handler.parse_range("bytes=0-1,4-5", 100)

    def test_publish_keeps_individual_files_without_streaming_bundle(self):
        payload = b"individual file payload" * 4096
        relative = ".minecraft/mods/example.jar"
        manifest = {
            "schemaVersion": "1.0",
            "productId": "test-client",
            "releaseId": "release-file-test",
            "version": "1.0.0",
            "publishedAt": "2026-07-29T00:00:00+00:00",
            "files": [
                {
                    "path": relative,
                    "size": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest().upper(),
                    "preserveExisting": False,
                }
            ],
        }
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            archive_path = temporary_root / "release.zip"
            with zipfile.ZipFile(
                    archive_path,
                    mode="w",
                    compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr(
                    "manifest.json",
                    json.dumps(manifest).encode("utf-8"))
                archive.writestr(
                    "payload/" + relative,
                    payload)

            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                published = server.publish_archive(archive_path)
            finally:
                server.DATA_ROOT = previous_root

            bundle_path = (
                temporary_root / "data" / "releases" /
                "test-client" / "release-file-test" /
                "bundle.tar.gz")
            self.assertFalse(bundle_path.exists())
            self.assertNotIn("bundle", published)
            current = json.loads((
                temporary_root / "data" / "current" /
                "test-client" / "manifest.json"
            ).read_text(encoding="utf-8"))
            self.assertNotIn("bundle", current)
            extracted = (
                temporary_root / "data" / "releases" /
                "test-client" / "release-file-test" /
                "files" / ".minecraft" / "mods" / "example.jar")
            self.assertEqual(payload, extracted.read_bytes())

    def test_successful_publish_removes_previous_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            first_archive = temporary_root / "first.zip"
            second_archive = temporary_root / "second.zip"
            self.create_release_archive(
                first_archive, "test-client", "release-one", "1.0.0")
            self.create_release_archive(
                second_archive, "test-client", "release-two", "1.0.1")

            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                server.publish_archive(first_archive)
                server.publish_archive(second_archive)
            finally:
                server.DATA_ROOT = previous_root

            product_releases = (
                temporary_root / "data" / "releases" / "test-client")
            self.assertFalse((product_releases / "release-one").exists())
            self.assertTrue((product_releases / "release-two").is_dir())
            self.assertEqual(
                ["release-two"],
                sorted(path.name for path in product_releases.iterdir()))

    def test_failed_current_switch_keeps_previous_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            first_archive = temporary_root / "first.zip"
            second_archive = temporary_root / "second.zip"
            self.create_release_archive(
                first_archive, "test-client", "release-one", "1.0.0")
            self.create_release_archive(
                second_archive, "test-client", "release-two", "1.0.1")

            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                server.publish_archive(first_archive)
                original_copyfile = server.shutil.copyfile

                def fail_current_manifest(source, destination):
                    if Path(destination).parent == server.DATA_ROOT / "current":
                        raise OSError("simulated current switch failure")
                    return original_copyfile(source, destination)

                with mock.patch.object(
                        server.shutil,
                        "copyfile",
                        side_effect=fail_current_manifest):
                    with self.assertRaisesRegex(
                            OSError,
                            "simulated current switch failure"):
                        server.publish_archive(second_archive)
            finally:
                server.DATA_ROOT = previous_root

            product_releases = (
                temporary_root / "data" / "releases" / "test-client")
            self.assertTrue((product_releases / "release-one").is_dir())
            self.assertFalse((product_releases / "release-two").exists())
            current = json.loads((
                temporary_root / "data" / "current" /
                "test-client" / "manifest.json"
            ).read_text(encoding="utf-8"))
            self.assertEqual("release-one", current["releaseId"])

    def test_launcher_publish_removes_previous_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                first = temporary_root / "first.exe"
                first.write_bytes(b"first launcher")
                server.publish_launcher(
                    "test-client",
                    "1.0.0",
                    first,
                    len(b"first launcher"),
                    hashlib.sha256(b"first launcher").hexdigest().upper(),
                )
                second = temporary_root / "second.exe"
                second.write_bytes(b"second launcher")
                server.publish_launcher(
                    "test-client",
                    "1.0.1",
                    second,
                    len(b"second launcher"),
                    hashlib.sha256(b"second launcher").hexdigest().upper(),
                )
            finally:
                server.DATA_ROOT = previous_root

            launcher_root = (
                temporary_root / "data" / "launchers" / "test-client")
            self.assertFalse((launcher_root / "1.0.0").exists())
            self.assertTrue((launcher_root / "1.0.1" / "setup.exe").is_file())

    def test_identical_launcher_retry_is_idempotent(self):
        content = b"same launcher"
        sha256 = hashlib.sha256(content).hexdigest().upper()
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                first = temporary_root / "first.exe"
                first.write_bytes(content)
                expected = server.publish_launcher(
                    "test-client", "1.0.0", first, len(content), sha256)
                retry = temporary_root / "retry.exe"
                retry.write_bytes(content)
                actual = server.publish_launcher(
                    "test-client", "1.0.0", retry, len(content), sha256)
            finally:
                server.DATA_ROOT = previous_root

            self.assertEqual(expected, actual)
            self.assertFalse(retry.exists())

    def test_same_launcher_version_with_different_content_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            previous_root = server.DATA_ROOT
            server.DATA_ROOT = temporary_root / "data"
            try:
                first = temporary_root / "first.exe"
                first.write_bytes(b"first")
                server.publish_launcher(
                    "test-client",
                    "1.0.0",
                    first,
                    len(b"first"),
                    hashlib.sha256(b"first").hexdigest().upper(),
                )
                different = temporary_root / "different.exe"
                different.write_bytes(b"different")
                with self.assertRaisesRegex(
                        FileExistsError,
                        "different content"):
                    server.publish_launcher(
                        "test-client",
                        "1.0.0",
                        different,
                        len(b"different"),
                        hashlib.sha256(b"different").hexdigest().upper(),
                    )
            finally:
                server.DATA_ROOT = previous_root


if __name__ == "__main__":
    unittest.main()
