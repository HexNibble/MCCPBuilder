#!/usr/bin/env python3
"""MCCP update repository HTTP service.

The service is deliberately dependency-free. Nginx terminates HTTPS and proxies
requests to this process on 127.0.0.1.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import tempfile
import threading
import time
import urllib.parse
import zipfile
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path, PurePosixPath


LISTEN_HOST = os.environ.get("MCCP_UPDATE_HOST", "127.0.0.1")
LISTEN_PORT = int(os.environ.get("MCCP_UPDATE_PORT", "18080"))
DATA_ROOT = Path(os.environ.get(
    "MCCP_UPDATE_DATA",
    "/var/lib/mccp-update"))
KEY_FILE = Path(os.environ.get(
    "MCCP_UPDATE_KEY_FILE",
    "/etc/mccp-update/publisher.key"))
MAX_UPLOAD_BYTES = int(os.environ.get(
    "MCCP_UPDATE_MAX_UPLOAD_BYTES",
    str(16 * 1024**3)))
MAX_FILE_COUNT = int(os.environ.get(
    "MCCP_UPDATE_MAX_FILE_COUNT",
    "200000"))
MAX_EXPANDED_BYTES = int(os.environ.get(
    "MCCP_UPDATE_MAX_EXPANDED_BYTES",
    str(24 * 1024**3)))
MAX_CLOCK_SKEW_SECONDS = 300
RELEASE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
PRODUCT_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")
VERSION_PATTERN = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
MAX_LAUNCHER_BYTES = 2 * 1024**3

_publish_lock = threading.Lock()
_launcher_lock = threading.Lock()
_policy_lock = threading.Lock()
_nonce_lock = threading.Lock()
_recent_nonces: dict[str, float] = {}


def remove_obsolete_version_directories(
        product_root: Path,
        keep_name: str) -> list[str]:
    """Remove direct child version directories except the active version."""
    removed: list[str] = []
    if not product_root.is_dir():
        return removed

    for candidate in product_root.iterdir():
        if candidate.name == keep_name:
            continue
        try:
            if candidate.is_symlink():
                candidate.unlink()
            elif candidate.is_dir():
                shutil.rmtree(candidate)
            else:
                continue
            removed.append(candidate.name)
        except OSError as exception:
            print(
                "Unable to remove obsolete version "
                f"{candidate}: {exception}",
                flush=True,
            )
    return removed


def json_bytes(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")


def read_key() -> bytes:
    encoded = KEY_FILE.read_text(encoding="ascii").strip()
    key = base64.b64decode(encoded, validate=True)
    if len(key) != 32:
        raise ValueError("publisher key must contain exactly 32 bytes")
    return key


def safe_relative_path(value: str) -> PurePosixPath:
    if not value or "\\" in value or "\x00" in value:
        raise ValueError("invalid relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError("unsafe relative path")
    return path


def current_manifest_path(product_id: str) -> Path:
    return DATA_ROOT / "current" / product_id / "manifest.json"


def policy_path(product_id: str) -> Path:
    return DATA_ROOT / "policies" / f"{product_id}.json"


def launcher_manifest_path(product_id: str) -> Path:
    return DATA_ROOT / "launcher-current" / f"{product_id}.json"


def version_tuple(version: str) -> tuple[int, int, int]:
    match = VERSION_PATTERN.fullmatch(version)
    if match is None:
        raise ValueError("launcher version must use x.y.z format")
    parts = tuple(int(value) for value in match.groups())
    if any(value > 2147483647 for value in parts):
        raise ValueError("launcher version component is too large")
    return parts


def load_launcher(product_id: str) -> dict | None:
    path = launcher_manifest_path(product_id)
    if not path.is_file():
        return None
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        version_tuple(str(value.get("version", "")))
        size = value.get("size")
        sha256 = str(value.get("sha256", "")).upper()
        if (
            type(size) is not int
            or size <= 0
            or size > MAX_LAUNCHER_BYTES
            or not re.fullmatch(r"[A-F0-9]{64}", sha256)
        ):
            raise ValueError("invalid launcher package metadata")
        return {
            "version": str(value["version"]),
            "size": size,
            "sha256": sha256,
        }
    except (OSError, ValueError, json.JSONDecodeError):
        return None


def publish_launcher(
        product_id: str,
        version: str,
        uploaded_file: Path,
        size: int,
        sha256: str) -> dict:
    incoming_version = version_tuple(version)
    current = load_launcher(product_id)
    if current is not None:
        current_version = version_tuple(str(current["version"]))
        if incoming_version == current_version:
            if (
                    size == int(current["size"])
                    and hmac.compare_digest(
                        sha256.upper(),
                        str(current["sha256"]).upper())):
                uploaded_file.unlink(missing_ok=True)
                return current
            raise FileExistsError(
                "launcher version already exists with different content")
        if incoming_version < current_version:
            raise FileExistsError(
                "launcher version must be newer than the current version")

    destination = (
        DATA_ROOT / "launchers" / product_id / version / "setup.exe")
    if destination.exists():
        raise FileExistsError("launcher package version already exists")
    destination.parent.mkdir(parents=True, exist_ok=True)
    os.replace(uploaded_file, destination)

    manifest = {
        "version": version,
        "size": size,
        "sha256": sha256,
    }
    current_path = launcher_manifest_path(product_id)
    current_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = current_path.with_name(
        f".{current_path.name}.{secrets.token_hex(8)}.tmp")
    try:
        temporary.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        os.replace(temporary, current_path)
    except Exception:
        destination.unlink(missing_ok=True)
        raise
    finally:
        temporary.unlink(missing_ok=True)
    remove_obsolete_version_directories(
        destination.parent.parent,
        version,
    )
    return manifest


def default_policy() -> dict:
    return {
        "showMessage": False,
        "title": "",
        "message": "",
        "blockLaunch": False,
    }


def validate_policy(value: object) -> dict:
    if not isinstance(value, dict):
        raise ValueError("policy must be a JSON object")
    policy = {
        "showMessage": value.get("showMessage", False),
        "title": value.get("title", ""),
        "message": value.get("message", ""),
        "blockLaunch": value.get("blockLaunch", False),
    }
    if (
        type(policy["showMessage"]) is not bool
        or type(policy["blockLaunch"]) is not bool
        or not isinstance(policy["title"], str)
        or not isinstance(policy["message"], str)
        or len(policy["title"]) > 128
        or len(policy["message"]) > 4000
        or (
            (policy["showMessage"] or policy["blockLaunch"])
            and not policy["message"].strip()
        )
    ):
        raise ValueError("invalid client launch policy")
    return policy


def load_policy(product_id: str) -> dict:
    path = policy_path(product_id)
    if not path.is_file():
        return default_policy()
    try:
        return validate_policy(json.loads(path.read_text(encoding="utf-8")))
    except (OSError, ValueError, json.JSONDecodeError):
        # A damaged policy must fail closed for game launch.
        return {
            "showMessage": True,
            "title": "服务器配置异常",
            "message": "服务器启动策略配置无效，请联系维护人员。",
            "blockLaunch": True,
        }


def save_policy(product_id: str, policy: dict) -> None:
    destination = policy_path(product_id)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(
        f".{destination.name}.{secrets.token_hex(8)}.tmp")
    try:
        temporary.write_text(
            json.dumps(policy, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def clean_nonces(now: float) -> None:
    expired = [
        nonce for nonce, seen_at in _recent_nonces.items()
        if now - seen_at > MAX_CLOCK_SKEW_SECONDS * 2
    ]
    for nonce in expired:
        _recent_nonces.pop(nonce, None)


def protocol_header(headers, name: str) -> str:
    return headers.get(f"X-MCCP-{name}", "")


def authenticate_publish(headers) -> tuple[bool, str]:
    timestamp_text = protocol_header(headers, "Timestamp")
    nonce = protocol_header(headers, "Nonce")
    content_hash = protocol_header(
        headers,
        "Content-SHA256").upper()
    signature = protocol_header(headers, "Signature").upper()
    if not (
        timestamp_text.isdigit()
        and re.fullmatch(r"[A-F0-9]{64}", content_hash)
        and re.fullmatch(r"[A-F0-9]{64}", signature)
        and re.fullmatch(r"[A-Za-z0-9_-]{16,128}", nonce)
    ):
        return False, "missing or malformed authentication headers"

    now = time.time()
    if abs(now - int(timestamp_text)) > MAX_CLOCK_SKEW_SECONDS:
        return False, "request timestamp is outside the allowed window"

    message = f"{timestamp_text}\n{nonce}\n{content_hash}".encode("ascii")
    expected = hmac.new(read_key(), message, hashlib.sha256).hexdigest().upper()
    if not hmac.compare_digest(signature, expected):
        return False, "publisher key validation failed"

    with _nonce_lock:
        clean_nonces(now)
        if nonce in _recent_nonces:
            return False, "request nonce has already been used"
        _recent_nonces[nonce] = now
    return True, content_hash


def validate_and_extract(archive: Path, staging: Path) -> dict:
    with zipfile.ZipFile(archive, "r") as bundle:
        infos = bundle.infolist()
        if len(infos) > MAX_FILE_COUNT + 1:
            raise ValueError("release contains too many files")
        total_expanded = sum(info.file_size for info in infos)
        if total_expanded > MAX_EXPANDED_BYTES:
            raise ValueError("expanded release is too large")

        names = {info.filename for info in infos}
        if "manifest.json" not in names:
            raise ValueError("release is missing manifest.json")
        manifest = json.loads(bundle.read("manifest.json"))
        release_id = str(manifest.get("releaseId", ""))
        if not RELEASE_ID_PATTERN.fullmatch(release_id):
            raise ValueError("invalid releaseId")
        product_id = str(manifest.get("productId", ""))
        if not PRODUCT_ID_PATTERN.fullmatch(product_id):
            raise ValueError("invalid productId")

        entries = manifest.get("files")
        if not isinstance(entries, list) or not entries:
            raise ValueError("manifest files must be a non-empty array")
        if len(entries) > MAX_FILE_COUNT:
            raise ValueError("manifest contains too many files")

        expected_names: set[str] = {"manifest.json"}
        for entry in entries:
            relative = safe_relative_path(str(entry.get("path", "")))
            zip_name = "payload/" + relative.as_posix()
            if zip_name in expected_names:
                raise ValueError(f"duplicate payload path: {relative}")
            expected_names.add(zip_name)

            info = bundle.getinfo(zip_name)
            mode = info.external_attr >> 16
            if info.is_dir() or (mode & 0o170000) == 0o120000:
                raise ValueError(f"unsupported payload entry: {relative}")
            expected_size = int(entry.get("size", -1))
            expected_hash = str(entry.get("sha256", "")).upper()
            preserve_existing = entry.get("preserveExisting", False)
            if info.file_size != expected_size:
                raise ValueError(f"size mismatch: {relative}")
            if not re.fullmatch(r"[A-F0-9]{64}", expected_hash):
                raise ValueError(f"invalid SHA-256: {relative}")
            if type(preserve_existing) is not bool:
                raise ValueError(
                    f"invalid preserveExisting flag: {relative}")

        extra_names = {
            name for name in names
            if not name.endswith("/") and name not in expected_names
        }
        if extra_names:
            raise ValueError("archive contains files not listed in manifest")

        files_root = staging / "files"
        files_root.mkdir(parents=True)
        for entry in entries:
            relative = safe_relative_path(str(entry["path"]))
            destination = files_root.joinpath(*relative.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            hasher = hashlib.sha256()
            with bundle.open("payload/" + relative.as_posix()) as source:
                with destination.open("xb") as output:
                    while chunk := source.read(1024 * 1024):
                        hasher.update(chunk)
                        output.write(chunk)
            actual_hash = hasher.hexdigest().upper()
            if actual_hash != str(entry["sha256"]).upper():
                raise ValueError(f"SHA-256 mismatch: {relative}")

        (staging / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return manifest


def publish_archive(
        archive: Path,
        remove_archive_after_validation: bool = False) -> dict:
    releases = DATA_ROOT / "releases"
    current = DATA_ROOT / "current"
    staging_parent = DATA_ROOT / "staging"
    releases.mkdir(parents=True, exist_ok=True)
    current.mkdir(parents=True, exist_ok=True)
    staging_parent.mkdir(parents=True, exist_ok=True)

    staging = Path(tempfile.mkdtemp(prefix="release-", dir=staging_parent))
    destination: Path | None = None
    temporary_manifest: Path | None = None
    current_switched = False
    try:
        manifest = validate_and_extract(archive, staging)
        if remove_archive_after_validation:
            archive.unlink(missing_ok=True)
        release_id = str(manifest["releaseId"])
        product_id = str(manifest["productId"])
        (staging / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        destination = releases / product_id / release_id
        if destination.exists():
            raise FileExistsError("releaseId already exists")
        destination.parent.mkdir(parents=True, exist_ok=True)
        os.replace(staging, destination)

        product_current = current / product_id
        product_current.mkdir(parents=True, exist_ok=True)
        temporary_manifest = current / (
            f".manifest.{secrets.token_hex(8)}.tmp")
        shutil.copyfile(destination / "manifest.json", temporary_manifest)
        os.replace(temporary_manifest, product_current / "manifest.json")
        current_switched = True
        remove_obsolete_version_directories(
            destination.parent,
            release_id,
        )
        return manifest
    finally:
        if temporary_manifest is not None:
            temporary_manifest.unlink(missing_ok=True)
        if (
                destination is not None
                and destination.exists()
                and not current_switched):
            shutil.rmtree(destination, ignore_errors=True)
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)


class Handler(BaseHTTPRequestHandler):
    server_version = "MCCPUpdateServer/1.2"

    def log_message(self, format_string: str, *args) -> None:
        # Authentication headers are intentionally never logged.
        print(
            f"{self.address_string()} - "
            f"{format_string % args}",
            flush=True,
        )

    def send_json(self, status: HTTPStatus, value: object) -> None:
        body = json_bytes(value)
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    @staticmethod
    def parse_range(value: str, size: int) -> tuple[int, int]:
        match = re.fullmatch(r"bytes=(\d*)-(\d*)", value.strip())
        if match is None or size <= 0:
            raise ValueError("invalid Range header")
        start_text, end_text = match.groups()
        if not start_text:
            if not end_text:
                raise ValueError("empty byte range")
            suffix = int(end_text)
            if suffix <= 0:
                raise ValueError("invalid suffix byte range")
            start = max(0, size - suffix)
            return start, size - 1

        start = int(start_text)
        end = int(end_text) if end_text else size - 1
        if start >= size or end < start:
            raise ValueError("byte range is outside the file")
        return start, min(end, size - 1)

    def send_file(
            self,
            path: Path,
            content_type: str,
            immutable: bool = True) -> None:
        size = path.stat().st_size
        range_header = self.headers.get("Range")
        start = 0
        end = size - 1
        status = HTTPStatus.OK
        if range_header:
            try:
                start, end = self.parse_range(range_header, size)
                status = HTTPStatus.PARTIAL_CONTENT
            except (ValueError, OverflowError):
                self.send_response(
                    HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return

        length = end - start + 1
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if status == HTTPStatus.PARTIAL_CONTENT:
            self.send_header(
                "Content-Range",
                f"bytes {start}-{end}/{size}")
        if immutable:
            self.send_header(
                "Cache-Control",
                "public, max-age=31536000, immutable")
        else:
            self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if self.command == "HEAD":
            return

        remaining = length
        with path.open("rb") as source:
            source.seek(start)
            while remaining:
                chunk = source.read(min(1024 * 1024, remaining))
                if not chunk:
                    raise ConnectionError(
                        "file ended before the requested range")
                self.wfile.write(chunk)
                remaining -= len(chunk)

    def do_GET(self) -> None:
        self.handle_read()

    def do_HEAD(self) -> None:
        self.handle_read()

    def handle_read(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path == "/v1/health":
            self.send_json(HTTPStatus.OK, {
                "status": "ok",
                "features": {
                    "httpRange": True,
                    "downloadMode": "individual-files",
                    "streamingBundle": False,
                    "singleVersionRetention": True,
                },
            })
            return
        product_prefix = "/v1/products/"
        if parsed.path.startswith(product_prefix) and parsed.path.endswith(
                "/manifest"):
            product_id = parsed.path[
                len(product_prefix):-len("/manifest")].strip("/")
            if not PRODUCT_ID_PATTERN.fullmatch(product_id):
                self.send_json(
                    HTTPStatus.BAD_REQUEST,
                    {"error": "invalid productId"},
                )
                return
            path = current_manifest_path(product_id)
            if not path.is_file():
                self.send_json(
                    HTTPStatus.SERVICE_UNAVAILABLE,
                    {"error": "no release has been published"},
                )
                return
            manifest = json.loads(path.read_text(encoding="utf-8"))
            manifest["policy"] = load_policy(product_id)
            manifest["launcher"] = load_launcher(product_id)
            body = json_bytes(manifest)
            self.send_response(HTTPStatus.OK)
            self.send_header(
                "Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)
            return
        launcher_prefix = "/v1/launchers/"
        if parsed.path.startswith(launcher_prefix):
            parts = parsed.path[len(launcher_prefix):].split("/")
            try:
                if (
                    len(parts) != 3
                    or parts[2] != "setup.exe"
                    or not PRODUCT_ID_PATTERN.fullmatch(parts[0])
                ):
                    raise ValueError("invalid launcher URL")
                version_tuple(parts[1])
                path = (
                    DATA_ROOT / "launchers" /
                    parts[0] / parts[1] / "setup.exe")
            except ValueError:
                self.send_json(
                    HTTPStatus.BAD_REQUEST,
                    {"error": "invalid launcher path"},
                )
                return
            if not path.is_file():
                self.send_json(
                    HTTPStatus.NOT_FOUND,
                    {"error": "launcher package not found"},
                )
                return
            self.send_file(
                path,
                "application/vnd.microsoft.portable-executable")
            return
        prefix = "/v1/files/"
        if parsed.path.startswith(prefix):
            parts = parsed.path[len(prefix):].split("/", 2)
            try:
                if (
                    len(parts) != 3
                    or not PRODUCT_ID_PATTERN.fullmatch(parts[0])
                    or not RELEASE_ID_PATTERN.fullmatch(parts[1])
                ):
                    raise ValueError("invalid release URL")
                relative = safe_relative_path(urllib.parse.unquote(parts[2]))
                release_root = (
                    DATA_ROOT / "releases" / parts[0] / parts[1] / "files")
                path = release_root.joinpath(*relative.parts)
                path.resolve().relative_to(release_root.resolve())
            except (ValueError, OSError):
                self.send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid path"})
                return
            if not path.is_file():
                self.send_json(HTTPStatus.NOT_FOUND, {"error": "file not found"})
                return
            self.send_file(path, "application/octet-stream")
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:
        parsed_path = urllib.parse.urlsplit(self.path).path
        product_prefix = "/v1/products/"
        if parsed_path.startswith(product_prefix) and parsed_path.endswith(
                "/launcher"):
            product_id = parsed_path[
                len(product_prefix):-len("/launcher")].strip("/")
            self.handle_launcher_publish(product_id)
            return
        if parsed_path.startswith(product_prefix) and parsed_path.endswith(
                "/policy"):
            product_id = parsed_path[
                len(product_prefix):-len("/policy")].strip("/")
            self.handle_policy_update(product_id)
            return
        if parsed_path != "/v1/publish":
            self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            return
        length_text = self.headers.get("Content-Length", "")
        if not length_text.isdigit():
            self.send_json(HTTPStatus.LENGTH_REQUIRED, {
                "error": "Content-Length is required"})
            return
        length = int(length_text)
        if length <= 0 or length > MAX_UPLOAD_BYTES:
            self.send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {
                "error": "upload is empty or too large"})
            return
        try:
            authenticated, result = authenticate_publish(self.headers)
        except (OSError, ValueError):
            self.send_json(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                {"error": "publisher key is unavailable"},
            )
            return
        if not authenticated:
            self.send_json(HTTPStatus.UNAUTHORIZED, {"error": result})
            return

        DATA_ROOT.mkdir(parents=True, exist_ok=True)
        descriptor, temporary_name = tempfile.mkstemp(
            prefix="upload-", suffix=".zip", dir=DATA_ROOT)
        archive = Path(temporary_name)
        try:
            hasher = hashlib.sha256()
            remaining = length
            with os.fdopen(descriptor, "wb") as output:
                while remaining:
                    chunk = self.rfile.read(min(1024 * 1024, remaining))
                    if not chunk:
                        raise ConnectionError("upload ended before Content-Length")
                    hasher.update(chunk)
                    output.write(chunk)
                    remaining -= len(chunk)
                output.flush()
                os.fsync(output.fileno())
            if not hmac.compare_digest(hasher.hexdigest().upper(), result):
                self.send_json(
                    HTTPStatus.BAD_REQUEST,
                    {"error": "uploaded file SHA-256 does not match signature"},
                )
                return
            with _publish_lock:
                manifest = publish_archive(
                    archive,
                    remove_archive_after_validation=True)
            self.send_json(HTTPStatus.CREATED, {
                "published": True,
                "productId": manifest["productId"],
                "releaseId": manifest["releaseId"],
                "fileCount": len(manifest["files"]),
            })
        except FileExistsError as exception:
            self.send_json(HTTPStatus.CONFLICT, {"error": str(exception)})
        except (
            ConnectionError,
            KeyError,
            OSError,
            ValueError,
            zipfile.BadZipFile,
        ) as exception:
            self.send_json(HTTPStatus.BAD_REQUEST, {"error": str(exception)})
        finally:
            archive.unlink(missing_ok=True)

    def handle_launcher_publish(self, product_id: str) -> None:
        if not PRODUCT_ID_PATTERN.fullmatch(product_id):
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": "invalid productId"},
            )
            return
        version = protocol_header(
            self.headers,
            "Launcher-Version").strip()
        try:
            version_tuple(version)
        except ValueError as exception:
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": str(exception)},
            )
            return
        length_text = self.headers.get("Content-Length", "")
        if not length_text.isdigit():
            self.send_json(
                HTTPStatus.LENGTH_REQUIRED,
                {"error": "Content-Length is required"},
            )
            return
        length = int(length_text)
        if length <= 0 or length > MAX_LAUNCHER_BYTES:
            self.send_json(
                HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                {"error": "launcher package is empty or too large"},
            )
            return
        try:
            authenticated, expected_hash = authenticate_publish(
                self.headers)
        except (OSError, ValueError):
            self.send_json(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                {"error": "publisher key is unavailable"},
            )
            return
        if not authenticated:
            self.send_json(
                HTTPStatus.UNAUTHORIZED,
                {"error": expected_hash},
            )
            return

        DATA_ROOT.mkdir(parents=True, exist_ok=True)
        descriptor, temporary_name = tempfile.mkstemp(
            prefix="launcher-", suffix=".exe", dir=DATA_ROOT)
        uploaded = Path(temporary_name)
        try:
            hasher = hashlib.sha256()
            remaining = length
            with os.fdopen(descriptor, "wb") as output:
                while remaining:
                    chunk = self.rfile.read(
                        min(1024 * 1024, remaining))
                    if not chunk:
                        raise ConnectionError(
                            "upload ended before Content-Length")
                    hasher.update(chunk)
                    output.write(chunk)
                    remaining -= len(chunk)
                output.flush()
                os.fsync(output.fileno())
            actual_hash = hasher.hexdigest().upper()
            if not hmac.compare_digest(
                    actual_hash, expected_hash):
                self.send_json(
                    HTTPStatus.BAD_REQUEST,
                    {
                        "error":
                            "launcher SHA-256 does not match signature"
                    },
                )
                return
            with _launcher_lock:
                manifest = publish_launcher(
                    product_id,
                    version,
                    uploaded,
                    length,
                    actual_hash,
                )
            self.send_json(HTTPStatus.CREATED, {
                "published": True,
                "productId": product_id,
                "launcher": manifest,
            })
        except FileExistsError as exception:
            self.send_json(
                HTTPStatus.CONFLICT,
                {"error": str(exception)},
            )
        except (ConnectionError, OSError, ValueError) as exception:
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": str(exception)},
            )
        finally:
            uploaded.unlink(missing_ok=True)

    def handle_policy_update(self, product_id: str) -> None:
        if not PRODUCT_ID_PATTERN.fullmatch(product_id):
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": "invalid productId"},
            )
            return
        length_text = self.headers.get("Content-Length", "")
        if not length_text.isdigit():
            self.send_json(
                HTTPStatus.LENGTH_REQUIRED,
                {"error": "Content-Length is required"},
            )
            return
        length = int(length_text)
        if length <= 0 or length > 64 * 1024:
            self.send_json(
                HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                {"error": "policy is empty or too large"},
            )
            return
        try:
            authenticated, expected_hash = authenticate_publish(self.headers)
        except (OSError, ValueError):
            self.send_json(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                {"error": "publisher key is unavailable"},
            )
            return
        if not authenticated:
            self.send_json(
                HTTPStatus.UNAUTHORIZED,
                {"error": expected_hash},
            )
            return

        body = self.rfile.read(length)
        if len(body) != length:
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": "policy body ended before Content-Length"},
            )
            return
        actual_hash = hashlib.sha256(body).hexdigest().upper()
        if not hmac.compare_digest(actual_hash, expected_hash):
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": "policy SHA-256 does not match signature"},
            )
            return
        try:
            policy = validate_policy(json.loads(body.decode("utf-8")))
            with _policy_lock:
                save_policy(product_id, policy)
            self.send_json(HTTPStatus.OK, {
                "updated": True,
                "productId": product_id,
                "policy": policy,
            })
        except (OSError, UnicodeDecodeError, ValueError, json.JSONDecodeError) as exception:
            self.send_json(
                HTTPStatus.BAD_REQUEST,
                {"error": str(exception)},
            )


def main() -> None:
    DATA_ROOT.mkdir(parents=True, exist_ok=True)
    read_key()
    server = ThreadingHTTPServer((LISTEN_HOST, LISTEN_PORT), Handler)
    print(
        f"MCCP update server listening on {LISTEN_HOST}:{LISTEN_PORT}",
        flush=True,
    )
    server.serve_forever()


if __name__ == "__main__":
    main()
