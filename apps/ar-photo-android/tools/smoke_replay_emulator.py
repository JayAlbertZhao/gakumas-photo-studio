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
import struct
import subprocess
import tempfile
import time
import uuid
import zlib
import xml.etree.ElementTree as ET


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


def saved_photo_rows(smoke, adb: str, serial: str) -> dict[int, int]:
    rows = smoke.run(adb, serial, "shell", "content", "query", "--uri",
                     "content://media/external/images/media", "--projection",
                     "_id:_size:mime_type:_display_name:is_pending")
    return {int(match.group(1)): int(match.group(2)) for match in re.finditer(
        r"_id=(\d+), _size=(\d+), mime_type=image/png, "
        r"_display_name=AR-Photo-[^,\s]+, is_pending=0", rows)}


def png_scene_metrics(png: bytes) -> tuple[int, tuple[int, int, int], tuple[int, int, int]]:
    """Sample the known replay fixture and camera floor, without Pillow."""
    if not png.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("emulator screenshot is not PNG")
    position = 8
    compressed = bytearray()
    width = height = 0
    while position + 12 <= len(png):
        length = struct.unpack_from(">I", png, position)[0]
        kind = png[position + 4:position + 8]
        chunk = png[position + 8:position + 8 + length]
        position += length + 12
        if kind == b"IHDR":
            width, height, depth, color, _, _, _ = struct.unpack(">IIBBBBB", chunk)
            if depth != 8 or color != 6:
                raise RuntimeError("expected an 8-bit RGBA emulator screenshot")
        elif kind == b"IDAT":
            compressed.extend(chunk)
        elif kind == b"IEND":
            break
    if not width or not height:
        raise RuntimeError("emulator screenshot has no dimensions")
    raw = zlib.decompress(compressed)
    stride = width * 4
    previous = bytearray(stride)
    cursor = 0
    count = 0
    top_pixel = floor_pixel = (0, 0, 0)
    for y in range(height):
        filter_type = raw[cursor]
        row = bytearray(raw[cursor + 1:cursor + 1 + stride])
        cursor += stride + 1
        for i in range(stride):
            left = row[i - 4] if i >= 4 else 0
            above = previous[i]
            upper_left = previous[i - 4] if i >= 4 else 0
            if filter_type == 1:
                predictor = left
            elif filter_type == 2:
                predictor = above
            elif filter_type == 3:
                predictor = (left + above) // 2
            elif filter_type == 4:
                estimate = left + above - upper_left
                distances = (abs(estimate - left), abs(estimate - above),
                             abs(estimate - upper_left))
                predictor = (left, above, upper_left)[distances.index(min(distances))]
            elif filter_type == 0:
                predictor = 0
            else:
                raise RuntimeError(f"unsupported PNG filter {filter_type}")
            row[i] = (row[i] + predictor) & 255
        if y == height // 10:
            x = width // 10
            top_pixel = tuple(row[x * 4:x * 4 + 3])
        if y == height * 85 // 100:
            x = width // 2
            floor_pixel = tuple(row[x * 4:x * 4 + 3])
        if height * 3 // 10 <= y < height * 6 // 10 and y % 4 == 0:
            for x in range(width * 4 // 10, width * 9 // 10, 4):
                red, green, blue = row[x * 4:x * 4 + 3]
                if 140 <= red <= 210 and 200 <= green <= 245 and 200 <= blue <= 250 \
                        and green > red + 20 and blue > red + 20:
                    count += 1
        previous = row
    return count, top_pixel, floor_pixel


def preview_model_pixels(png: bytes) -> int:
    return png_scene_metrics(png)[0]


def wait_for_playback_end(smoke, adb: str, serial: str, timeout: float = 150.0) -> None:
    deadline = time.monotonic() + timeout
    exited = 0
    last_texts = []
    while time.monotonic() < deadline:
        try:
            root = smoke.hierarchy(adb, serial)
            last_texts = [node.get("text", "") for node in root.iter("node")
                          if node.get("text")]
        except (subprocess.CalledProcessError, ET.ParseError):
            time.sleep(3)
            continue
        if any(value.startswith("会话回放已结束") for value in last_texts):
            return
        if any(value.startswith("回放录制的相机") for value in last_texts):
            exited = 0
        else:
            exited += 1
            if exited >= 3:
                raise RuntimeError(f"replay mode exited before dataset end: {last_texts[:8]}")
        time.sleep(3)
    raise RuntimeError(f"dataset did not finish within {timeout:g}s: {last_texts[:8]}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True, help="adb emulator serial, e.g. emulator-5554")
    parser.add_argument("--dataset", type=Path, required=True,
                        help="local copy of SceneView's public bundled-pixel9-sample.mp4")
    parser.add_argument("--apk", type=Path,
                        default=ROOT / "app/build/outputs/apk/debug/app-debug.apk")
    parser.add_argument("--photo-output", type=Path,
                        help="optional local PNG for visual review; must be outside the checkout")
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        parser.error("only emulator-* serials are accepted")
    if not args.apk.is_file() or not args.dataset.is_file():
        parser.error("build the debug APK and provide the public ARCore dataset file")
    if args.photo_output is not None:
        output = args.photo_output.resolve()
        if output.is_relative_to(ROOT.parents[1]) or output.exists() or not output.parent.is_dir():
            parser.error("photo output must be a new file in an existing directory outside the checkout")
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
    # Filament's initial frame may still be blocking touches even though the
    # accessibility tree already contains the mode chip.
    for _ in range(3):
        time.sleep(1.5)
        tap_text(smoke, adb, args.serial, "数据回放")
        try:
            smoke.wait_for_prefix(adb, args.serial, "回放录制的相机", timeout=10.0)
            break
        except RuntimeError:
            continue
    else:
        raise RuntimeError("Data Replay chip never opened the AR scene")
    print("ARCore replay started; waiting for recorded session to finish", flush=True)
    wait_for_playback_end(smoke, adb, args.serial)

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
    before_photos = saved_photo_rows(smoke, adb, args.serial)
    tap_text(smoke, adb, args.serial, "收起控件")
    smoke.wait_for_text(adb, args.serial, "显示控件")
    tap_text(smoke, adb, args.serial, "拍照")
    deadline = time.monotonic() + 25.0
    new_photos = {}
    while time.monotonic() < deadline:
        new_photos = {photo_id: size for photo_id, size in
                      saved_photo_rows(smoke, adb, args.serial).items()
                      if photo_id not in before_photos}
        if new_photos:
            break
        time.sleep(0.5)
    if len(new_photos) != 1:
        raise RuntimeError(f"expected one newly saved PNG, got {new_photos}")
    photo_id, photo_size = next(iter(new_photos.items()))
    if photo_size < 100_000:
        raise RuntimeError(f"saved photo is unexpectedly small: {photo_size} bytes")
    photo_uri = f"content://media/external/images/media/{photo_id}"
    metadata = smoke.run(adb, args.serial, "shell", "content", "query", "--uri", photo_uri,
                         "--projection", "_size:mime_type:_display_name:is_pending")
    if "mime_type=image/png" not in metadata or "is_pending=0" not in metadata:
        raise RuntimeError(f"saved photo missing or incomplete: {metadata}")
    photo_bytes = subprocess.run([adb, "-s", args.serial, "exec-out", "content",
                                  "read", "--uri", photo_uri], check=True,
                                 capture_output=True).stdout
    if not photo_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("MediaStore returned a non-PNG photo")
    if args.photo_output is not None:
        output.write_bytes(photo_bytes)
        print(f"Review photo copied to {output}", flush=True)
    model_pixels, top_pixel, floor_pixel = png_scene_metrics(photo_bytes)
    if (model_pixels < 2_000 or top_pixel[0] >= 225
            or not (75 <= floor_pixel[0] < 220 and
                    floor_pixel[0] > floor_pixel[1] + 15 and
                    floor_pixel[1] > floor_pixel[2] + 5)):
        raise RuntimeError("saved PNG lacks fixture/camera or still contains controls: "
                           f"model={model_pixels}, top={top_pixel}, floor={floor_pixel}")
    root = smoke.wait_for_text(adb, args.serial, "显示控件")
    if smoke.find_text(root, "收起控件") is not None:
        raise RuntimeError("full controls reopened after collapsed-view capture")
    # This is a disposable emulator test artifact, not a user photograph.
    smoke.run(adb, args.serial, "shell", "content", "delete", "--uri", photo_uri)
    print("Collapsed-view PNG saved; controls remained collapsed; test copy removed", flush=True)
    tap_text(smoke, adb, args.serial, "显示控件")
    tap_text(smoke, adb, args.serial, "重新播放会话")
    smoke.wait_for_prefix(adb, args.serial, "正在从头播放会话")
    tap_text(smoke, adb, args.serial, "合成预览")
    smoke.wait_for_text(adb, args.serial, "Slide")
    # The Compose label can update before the new Filament scene draws its
    # first imported frame. Wait for pixels, not just accessibility text.
    preview_deadline = time.monotonic() + 25.0
    visible_pixels = 0
    while time.monotonic() < preview_deadline:
        screenshot = subprocess.run([adb, "-s", args.serial, "exec-out", "screencap", "-p"],
                                    check=True, capture_output=True).stdout
        visible_pixels = preview_model_pixels(screenshot)
        if visible_pixels >= 2_000:
            break
        time.sleep(1.0)
    else:
        raise RuntimeError(f"imported model absent after returning to preview: "
                           f"{visible_pixels} fixture-color samples")
    final_pid = smoke.run(adb, args.serial, "shell", "pidof", smoke.PACKAGE)
    if not initial_pid or final_pid != initial_pid:
        raise RuntimeError(f"app process changed during replay/mode switch: {initial_pid} -> {final_pid}")
    print("PASS: replay, floor anchor, clip switch, PNG save, restart, visible preview return",
          flush=True)


if __name__ == "__main__":
    main()
