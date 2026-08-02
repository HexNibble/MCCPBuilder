#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import grp
import hashlib
import os
import secrets
from pathlib import Path


KEY_PATH = Path("/etc/mccp-update/publisher.key")
SERVICE_GROUP = "mccp-update"


def load_key() -> bytes:
    key = base64.b64decode(
        KEY_PATH.read_text(encoding="ascii").strip(),
        validate=True,
    )
    if len(key) != 32:
        raise SystemExit("密钥文件格式无效。")
    return key


def fingerprint(key: bytes) -> str:
    return hashlib.sha256(key).hexdigest().upper()


def rotate() -> None:
    KEY_PATH.parent.mkdir(parents=True, exist_ok=True)
    temporary = KEY_PATH.with_name(f".{KEY_PATH.name}.{secrets.token_hex(8)}")
    temporary.write_text(
        base64.b64encode(secrets.token_bytes(32)).decode("ascii") + "\n",
        encoding="ascii",
    )
    os.chown(temporary, 0, grp.getgrnam(SERVICE_GROUP).gr_gid)
    os.chmod(temporary, 0o640)
    os.replace(temporary, KEY_PATH)
    print(f"已生成新密钥文件：{KEY_PATH}")
    print(f"密钥指纹：{fingerprint(load_key())}")
    print("旧密钥立即失效，无需重启更新服务。")


def show() -> None:
    key = load_key()
    print(f"密钥文件：{KEY_PATH}")
    print(f"密钥指纹：{fingerprint(key)}")
    print("权限应为 640，所有者应为 root:mccp-update。")


def export(destination: str) -> None:
    key = load_key()
    path = Path(destination).expanduser().resolve()
    if path.is_dir():
        path = path / "mccp-publisher.key"
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(base64.b64encode(key) + b"\n")
    print(f"密钥副本已写入：{path}")
    print(f"密钥指纹：{fingerprint(key)}")


parser = argparse.ArgumentParser(description="管理 MCCP 更新服务器的发布密钥文件")
parser.add_argument("action", choices=("show", "rotate", "export"))
parser.add_argument("destination", nargs="?")
arguments = parser.parse_args()

if arguments.action == "show":
    show()
elif arguments.action == "rotate":
    rotate()
elif arguments.action == "export":
    if not arguments.destination:
        parser.error("export 需要目标路径")
    export(arguments.destination)
