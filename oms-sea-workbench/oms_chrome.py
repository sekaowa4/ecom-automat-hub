"""Launch a persistent, isolated Chrome profile for OMS work in UpSeller."""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from collections.abc import Mapping
from pathlib import Path


DEBUG_PORT = 9223
UPSELLER_URL = "https://app.upseller.com/"


def find_chrome(env: Mapping[str, str] | None = None) -> Path | None:
    env = os.environ if env is None else env
    candidates = [
        env.get("OMS_CHROME_PATH"),
        str(Path(env.get("ProgramFiles", r"C:\Program Files")) / "Google/Chrome/Application/chrome.exe"),
        str(Path(env.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Google/Chrome/Application/chrome.exe"),
        str(Path(env.get("LOCALAPPDATA", "")) / "Google/Chrome/Application/chrome.exe"),
    ]
    return next((Path(path) for path in candidates if path and Path(path).is_file()), None)


def profile_directory(env: Mapping[str, str] | None = None) -> Path:
    env = os.environ if env is None else env
    local_app_data = env.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("未找到 LOCALAPPDATA，无法安全保存专用 Chrome 登录状态")
    return Path(local_app_data) / "OMS海运录入" / "chrome-profile"


def build_launch_args(profile_dir: Path) -> list[str]:
    return [
        f"--remote-debugging-port={DEBUG_PORT}",
        f"--user-data-dir={profile_dir}",
        UPSELLER_URL,
    ]


def launch_chrome(
    env: Mapping[str, str] | None = None,
    popen=subprocess.Popen,
) -> Path:
    env = os.environ if env is None else env
    chrome = find_chrome(env)
    if chrome is None:
        raise FileNotFoundError("未找到 Google Chrome；请安装 Chrome 或设置 OMS_CHROME_PATH")
    profile = profile_directory(env)
    profile.mkdir(parents=True, exist_ok=True)
    popen([str(chrome), *build_launch_args(profile)], close_fds=True)
    return profile


def main() -> int:
    parser = argparse.ArgumentParser(description="启动 OMS 专用 Chrome 并打开 UpSeller")
    parser.parse_args()
    try:
        profile = launch_chrome()
    except (FileNotFoundError, OSError, RuntimeError) as error:
        print(f"启动失败：{error}", file=sys.stderr)
        return 1
    print(f"OMS 专用 Chrome 已启动；登录资料保存在：{profile}")
    print(f"本机调试端口：http://127.0.0.1:{DEBUG_PORT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
