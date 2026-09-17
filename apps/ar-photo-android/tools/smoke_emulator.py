"""Smoke-test animated GLB rendering on a disposable Android emulator.

This replaces the debug app's private imported model. It never targets a phone.
Build :app:assembleDebug before running this script.
"""

import argparse
import importlib.util
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
PACKAGE = "org.digital_kotone.arphoto"
BOUNDS = re.compile(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]")


def adb_executable() -> str:
    command = shutil.which("adb")
    if command:
        return command
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if sdk:
        candidate = Path(sdk) / "platform-tools" / ("adb.exe" if os.name == "nt" else "adb")
        if candidate.is_file():
            return str(candidate)
    raise RuntimeError("adb not found; add Android SDK platform-tools to PATH")


def run(adb: str, serial: str, *args: str) -> str:
    result = subprocess.run([adb, "-s", serial, *args], check=True,
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
    return result.stdout.strip()


def bounds_center(raw: str, fraction: float = 0.5) -> tuple[int, int]:
    match = BOUNDS.fullmatch(raw)
    if not match:
        raise RuntimeError(f"unexpected accessibility bounds: {raw}")
    left, top, right, bottom = map(int, match.groups())
    return (round(left + (right - left) * fraction), (top + bottom) // 2)


def hierarchy(adb: str, serial: str) -> ET.Element:
    remote = "/sdcard/ar-photo-smoke-ui.xml"
    run(adb, serial, "shell", "uiautomator", "dump", remote)
    return ET.fromstring(run(adb, serial, "shell", "cat", remote))


def find_text(root: ET.Element, value: str) -> ET.Element | None:
    return next((node for node in root.iter("node") if node.get("text") == value), None)


def size_label(root: ET.Element) -> str | None:
    return next((value for node in root.iter("node")
                 if (value := node.get("text", "")).startswith("大小 ")), None)


def wait_for_text(adb: str, serial: str, value: str, timeout: float = 25.0) -> ET.Element:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        # The accessibility bridge can be unavailable for a few seconds just
        # after Activity launch; uiautomator may even report success without
        # writing its XML file in that interval.
        try:
            root = hierarchy(adb, serial)
            if find_text(root, value) is not None:
                return root
        except (subprocess.CalledProcessError, ET.ParseError):
            pass
        time.sleep(0.5)
    raise RuntimeError(f"app did not show {value!r} within {timeout:g}s")


def tap(adb: str, serial: str, point: tuple[int, int]) -> None:
    run(adb, serial, "shell", "input", "tap", str(point[0]), str(point[1]))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True, help="adb emulator serial, e.g. emulator-5554")
    parser.add_argument("--apk", type=Path,
                        default=ROOT / "app/build/outputs/apk/debug/app-debug.apk")
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        parser.error("only emulator-* serials are accepted; this test replaces app-private data")
    if not args.apk.is_file():
        parser.error(f"debug APK not found: {args.apk}; build :app:assembleDebug first")
    adb = adb_executable()
    if run(adb, args.serial, "shell", "getprop", "ro.kernel.qemu") != "1":
        parser.error("target is not an Android emulator")

    spec = importlib.util.spec_from_file_location("ar_photo_fixture", ROOT / "tools/generate_sample_glb.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    with tempfile.TemporaryDirectory(prefix="ar-photo-smoke-") as directory:
        fixture = Path(directory) / "animated.glb"
        fixture.write_bytes(module.make_glb(animated=True))
        remote = f"/data/local/tmp/ar-photo-smoke-{uuid.uuid4().hex}.glb"
        print(run(adb, args.serial, "install", "-r", str(args.apk)))
        run(adb, args.serial, "shell", "monkey", "-p", PACKAGE, "1")
        try:
            run(adb, args.serial, "push", str(fixture), remote)
            run(adb, args.serial, "shell", "run-as", PACKAGE, "mkdir", "-p", "files")
            run(adb, args.serial, "shell", "run-as", PACKAGE, "cp", remote, "files/subject.glb")
        finally:
            run(adb, args.serial, "shell", "rm", "-f", remote)

    run(adb, args.serial, "shell", "am", "force-stop", PACKAGE)
    run(adb, args.serial, "shell", "monkey", "-p", PACKAGE, "1")
    root = wait_for_text(adb, args.serial, "Bounce")
    initial_pid = run(adb, args.serial, "shell", "pidof", PACKAGE)
    next_button = find_text(root, "下一段")
    if next_button is None:
        raise RuntimeError("next-animation button not found")
    time.sleep(1.0)  # allow the freshly restored scene to become interactive
    tap(adb, args.serial, bounds_center(next_button.get("bounds")))
    root = wait_for_text(adb, args.serial, "Slide")
    slider = next((node for node in root.iter("node")
                   if node.get("class") == "android.widget.SeekBar"), None)
    if slider is None:
        raise RuntimeError("size slider not found")
    previous_size = size_label(root)
    if previous_size is None:
        raise RuntimeError("size label not found")
    tap(adb, args.serial, bounds_center(slider.get("bounds"), fraction=0.12))
    deadline = time.monotonic() + 10.0
    while time.monotonic() < deadline:
        root = hierarchy(adb, args.serial)
        if size_label(root) != previous_size:
            break
    else:
        raise RuntimeError(f"size label did not change from {previous_size!r}")
    if find_text(root, "Slide") is None:
        raise RuntimeError("selected Slide clip disappeared after resize")
    time.sleep(1.0)  # model-instance reload is coalesced after the gesture
    final_pid = run(adb, args.serial, "shell", "pidof", PACKAGE)
    if not initial_pid or final_pid != initial_pid:
        raise RuntimeError(f"app process changed during animation/resize: {initial_pid} -> {final_pid}")
    print(f"PASS: Bounce -> Slide, {previous_size} -> {size_label(root)}, "
          "Slide retained, app process remained alive")


if __name__ == "__main__":
    main()
