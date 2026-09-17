"""Generate a tiny, original glTF 2.0 GLB for testing the Android import flow.

No game assets, third-party models, or Python packages are required.
"""

import argparse
import json
import struct
from pathlib import Path


POSITIONS = (
    (0.0, 1.0, 0.0),
    (1.0, 0.0, 0.0),
    (0.0, 0.0, 1.0),
    (-1.0, 0.0, 0.0),
    (0.0, 0.0, -1.0),
    (0.0, -1.0, 0.0),
)
INDICES = (
    0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,
    5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4,
)


def make_glb(*, animated: bool = False) -> bytes:
    vertices = b"".join(struct.pack("<3f", *point) for point in POSITIONS)
    indices = struct.pack(f"<{len(INDICES)}H", *INDICES)
    binary = vertices + indices
    buffer_views = [
        {"buffer": 0, "byteOffset": 0, "byteLength": len(vertices), "target": 34962},
        {"buffer": 0, "byteOffset": len(vertices), "byteLength": len(indices), "target": 34963},
    ]
    accessors = [
        {"bufferView": 0, "componentType": 5126, "count": len(POSITIONS), "type": "VEC3",
         "min": [-1.0, -1.0, -1.0], "max": [1.0, 1.0, 1.0]},
        {"bufferView": 1, "componentType": 5123, "count": len(INDICES), "type": "SCALAR"},
    ]
    if animated:
        times = struct.pack("<3f", 0.0, 1.0, 2.0)
        bounce = struct.pack("<9f", 0.0, 0.0, 0.0, 0.0, 0.5, 0.0, 0.0, 0.0, 0.0)
        slide = struct.pack("<9f", 0.0, 0.0, 0.0, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0)
        for chunk in (times, bounce, slide):
            buffer_views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(chunk)})
            binary += chunk
        accessors.extend([
            {"bufferView": 2, "componentType": 5126, "count": 3, "type": "SCALAR",
             "min": [0.0], "max": [2.0]},
            {"bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC3",
             "min": [0.0, 0.0, 0.0], "max": [0.0, 0.5, 0.0]},
            {"bufferView": 4, "componentType": 5126, "count": 3, "type": "VEC3",
             "min": [0.0, 0.0, 0.0], "max": [0.5, 0.0, 0.0]},
        ])
    document = {
        "asset": {"version": "2.0", "generator": "photo-studio synthetic sample"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "Original test diamond"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "indices": 1, "material": 0}]}],
        "extensionsUsed": ["KHR_materials_unlit"],
        "materials": [{"name": "Synthetic teal", "doubleSided": True,
                       "pbrMetallicRoughness": {"baseColorFactor": [0.15, 0.75, 0.78, 1.0],
                                                "metallicFactor": 0.0, "roughnessFactor": 1.0},
                       "extensions": {"KHR_materials_unlit": {}}}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": buffer_views,
        "accessors": accessors,
    }
    if animated:
        document["animations"] = [
            {"name": name,
             "samplers": [{"input": 2, "output": output, "interpolation": "LINEAR"}],
             "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}
            for name, output in (("Bounce", 3), ("Slide", 4))
        ]
    json_chunk = json.dumps(document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_chunk += b" " * ((-len(json_chunk)) % 4)
    binary += b"\x00" * ((-len(binary)) % 4)
    total = 12 + 8 + len(json_chunk) + 8 + len(binary)
    return (struct.pack("<4sII", b"glTF", 2, total)
            + struct.pack("<I4s", len(json_chunk), b"JSON") + json_chunk
            + struct.pack("<I4s", len(binary), b"BIN\x00") + binary)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="destination .glb path")
    parser.add_argument("--animated", action="store_true", help="include two original node animations")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(make_glb(animated=args.animated))
    print(f"generated {args.output} ({args.output.stat().st_size} bytes)")
