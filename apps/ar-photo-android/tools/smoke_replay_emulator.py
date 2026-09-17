"""Replay SceneView's public Pixel 9 ARCore dataset on a disposable emulator.

The dataset is NOT included in this repository. This test replaces the debug
app's private model/dataset and grants camera permission on the named emulator.
It checks plane placement, clip switching, composited photo saving, replay
restart, and mode teardown.
"""

import argparse
import hashlib
import importlib.util
from pathlib import Path
import re
import tempfile
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
DATASET_SHA256 = "DB7371F42A515451B7FF7D0425B31059BA43A5AB800AB532FFFBD25D830932E7"


def load_smoke():
    spec = importlib.util.spec_from_file_location("ar_photo_smoke", ROOT / "tools/smoke_emulator.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def private_copy(smoke, adb: str, serial: str, source: Path, target: str) -> None:
    remote = f"/data/local/tmp/ar-photo-replay-{uuid.uuid4().hex}"
    try:
        smoke.run(adb, serial, "push", str(source), remote)
        smoke.run(adb, serial, "shell", "run-as", smoke.PACKAGE, "mkdir", "-p", "files")
        smoke.run(adb, serial, "shell", "run-as", smoke.PACKAGE, "cp", remote, f"files/{target}")
    finally:
        smoke.run(adb, serial, "shell", "rm", "-f", remote)


def tap_text(smoke, adb: str, serial: str, value: str) -> None:
    root = smoke.wait_for_text(adb, serial, value)
    node = smoke.find_text(root, value)
    smoke.tap(adb, serial, smoke.bounds_center(node.get("bounds")))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True, help="adb emulator serial, e.g. emulator-5554")
    parser.add_argument("--dataset", type=Path, required=True,
                        help="local copy of SceneView's public bundled-pixel9-sample.mp4")
    parser.add_argument("--apk", type=Path,
                        default=ROOT / "app/build/outputs/apk/debug/app-debug.apk")
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        parser.error("only emulator-* serials are accepted")
    if not args.apk.is_file() or not args.dataset.is_file():
        parser.error("build the debug APK and provide the public ARCore dataset file")
    digest = hashlib.sha256(args.dataset.read_bytes()).hexdigest().upper()
    if digest != DATASET_SHA256:
        parser.error("dataset SHA-256 differs from the reviewed public Pixel 9 recording")

    smoke = load_smoke()
    adb = smoke.adb_executable()
    if smoke.run(adb, args.serial, "shell", "getprop", "ro.kernel.qemu") != "1":
        parser.error("target is not an Android emulator")
    print(smoke.run(adb, args.serial, "install", "-r", str(args.apk)), flush=True)
    smoke.run(adb, args.serial, "shell", "am", "start", "-n", f"{smoke.PACKAGE}/.MainActivity")
    smoke.run(adb, args.serial, "shell", "pm", "grant", smoke.PACKAGE, "android.permission.CAMERA")
    private_copy(smoke, adb, args.serial, args.dataset, "session-playback.mp4")

    generator_spec = importlib.util.spec_from_file_location(
        "ar_photo_sample", ROOT / "tools/generate_sample_glb.py")
    generator = importlib.util.module_from_spec(generator_spec)
    generator_spec.loader.exec_module(generator)
    with tempfile.TemporaryDirectory(prefix="ar-photo-replay-") as directory:
        fixture = Path(directory) / "animated.glb"
        fixture.write_bytes(generator.make_glb(animated=True))
        private_copy(smoke, adb, args.serial, fixture, "subject.glb")
    print("Fixture and reviewed ARCore dataset installed in emulator-private storage", flush=True)

    smoke.run(adb, args.serial, "shell", "am", "force-stop", smoke.PACKAGE)
    smoke.run(adb, args.serial, "shell", "am", "start", "-n", f"{smoke.PACKAGE}/.MainActivity")
    smoke.wait_for_text(adb, args.serial, "Bounce")
    initial_pid = smoke.run(adb, args.serial, "shell", "pidof", smoke.PACKAGE)
    tap_text(smoke, adb, args.serial, "数据回放")
    smoke.wait_for_prefix(adb, args.serial, "回放录制的相机", timeout=30.0)
    print("ARCore replay started; waiting for recorded session to finish", flush=True)
    smoke.wait_for_prefix(adb, args.serial, "会话回放已结束", timeout=65.0)

    size = smoke.run(adb, args.serial, "shell", "wm", "size")
    match = re.search(r"(\d+)x(\d+)", size)
    if not match:
        raise RuntimeError(f"cannot determine emulator screen size: {size}")
    width, height = map(int, match.groups())
    for _ in range(3):
        smoke.tap(adb, args.serial, (round(width * 0.48), round(height * 0.49)))
        try:
            smoke.wait_for_prefix(adb, args.serial, "角色已放置", timeout=5.0)
            break
        except RuntimeError:
            time.sleep(0.5)
    else:
        raise RuntimeError("known floor hit did not place the model")
    print("Recorded floor accepted an anchor hit", flush=True)

    tap_text(smoke, adb, args.serial, "下一段")
    smoke.wait_for_text(adb, args.serial, "Slide")
    tap_text(smoke, adb, args.serial, "拍照")
    root = smoke.wait_for_prefix(adb, args.serial, "照片已保存：", timeout=25.0)
    message = next(node.get("text", "") for node in root.iter("node")
                   if node.get("text", "").startswith("照片已保存："))
    photo_uri = message.removeprefix("照片已保存：")
    if not re.fullmatch(r"content://media/external/images/media/\d+", photo_uri):
        raise RuntimeError(f"unexpected saved photo URI: {photo_uri}")
    metadata = smoke.run(adb, args.serial, "shell", "content", "query", "--uri", photo_uri,
                         "--projection", "_size:mime_type:_display_name:is_pending")
    size_match = re.search(r"_size=(\d+)", metadata)
    if (not size_match or int(size_match.group(1)) < 100_000
            or "mime_type=image/png" not in metadata or "is_pending=0" not in metadata):
        raise RuntimeError(f"saved photo missing or incomplete: {metadata}")
    # This is a disposable emulator test artifact, not a user photograph.
    smoke.run(adb, args.serial, "shell", "content", "delete", "--uri", photo_uri)
    print("Composited PNG was published to MediaStore and test copy removed", flush=True)
    tap_text(smoke, adb, args.serial, "重新播放会话")
    smoke.wait_for_prefix(adb, args.serial, "正在从头播放会话")
    tap_text(smoke, adb, args.serial, "合成预览")
    smoke.wait_for_text(adb, args.serial, "Slide")
    final_pid = smoke.run(adb, args.serial, "shell", "pidof", smoke.PACKAGE)
    if not initial_pid or final_pid != initial_pid:
        raise RuntimeError(f"app process changed during replay/mode switch: {initial_pid} -> {final_pid}")
    print("PASS: dataset replay, floor anchor, clip switch, PNG save, restart, synthetic return",
          flush=True)


if __name__ == "__main__":
    main()
