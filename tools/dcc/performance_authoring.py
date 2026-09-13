"""Independent baked DCC interchange. No Maya/Blender dependency for data sampling."""
from copy import deepcopy
import json
import math
import re
import struct

SCHEMA = "photo-studio.performance.v1"
PROPERTY = "photoStudioPerformance"
MAX_BYTES = 1048576


def float32(value):
    return struct.unpack("f", struct.pack("f", value))[0]


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", value):
        raise ValueError("Expected opaque binding ID")
    return value


def encode(clip):
    if clip.get("schema") != SCHEMA:
        raise ValueError("Unknown performance schema")
    text = json.dumps(clip, ensure_ascii=True, allow_nan=False, separators=(",", ":"))
    if len(text.encode("utf-8")) > MAX_BYTES:
        raise ValueError("Performance exceeds byte limit")
    return text


def bake(template, mappings, start_frame, end_frame, fps, read_attribute):
    """Sample explicit DCC attributes. read_attribute(attribute, frame) must return a finite scalar.

    Morph/decal values are piecewise linear samples, not an exact conversion of DCC weighted tangents.
    Effect/material enable attributes use left-sample >=0.5 intervals and reset local age each run.
    Coordinates in the template must already use the Unity host's explicit units/frame.
    """
    if (not isinstance(start_frame, int) or isinstance(start_frame, bool) or
            not isinstance(end_frame, int) or isinstance(end_frame, bool) or
            not 1 <= end_frame - start_frame <= 8191 or not math.isfinite(fps) or not 1 <= fps <= 240):
        raise ValueError("Invalid frame range/fps")
    clip = deepcopy(template)
    if clip.get("schema") != SCHEMA or (end_frame - start_frame) / fps > 3600:
        raise ValueError("Invalid schema/duration")
    clip["duration"] = float32((end_frame - start_frame) / fps)
    for collection in ("morphs", "decals", "effects", "materials", "locators"):
        clip.setdefault(collection, [])
    if len(mappings) > 512:
        raise ValueError("Mapping capacity exceeded")
    seen = set()
    for mapping in mappings:
        kind, target = mapping["kind"], identifier(mapping["target"])
        channel = mapping.get("channel")
        key = (kind, target, channel)
        if key in seen:
            raise ValueError("Duplicate mapping")
        seen.add(key)
        if kind not in ("morphs", "decals", "effects", "materials"):
            raise ValueError("Unknown mapping kind")
        if kind == "decals" and (not isinstance(channel, int) or isinstance(channel, bool) or not 0 <= channel < 28):
            raise ValueError("Invalid decal channel")
        if kind != "decals" and channel is not None:
            raise ValueError("Channel only applies to decals")
        field = "target" if kind in ("morphs", "decals") else "id"
        items = [item for item in clip[kind] if item[field] == target]
        if len(items) != 1:
            raise ValueError("Mapping must identify one existing definition")
        item = items[0]
        samples = []
        for frame in range(start_frame, end_frame + 1):
            value = float(read_attribute(mapping["attribute"], frame))
            if not math.isfinite(value) or abs(value) > (64 if kind == "morphs" else 1e6):
                raise ValueError("Invalid sampled scalar")
            samples.append(dict(seconds=float32((frame - start_frame) / fps), value=value, inTangent=0, outTangent=0, interpolation=1))
        if kind == "morphs":
            item["keys"] = samples
        elif kind == "decals":
            tracks = [track for track in item.setdefault("tracks", []) if track["channel"] != channel]
            item["tracks"] = tracks + [dict(channel=channel, keys=samples)]
        else:
            runs, beginning = [], None
            for i, sample in enumerate(samples):
                active = i + 1 < len(samples) and sample["value"] >= .5
                if active and beginning is None:
                    beginning = sample["seconds"]
                if not active and beginning is not None:
                    entry = deepcopy(item)
                    entry.update(id=identifier(target + "." + str(len(runs))), start=beginning, duration=sample["seconds"] - beginning)
                    if kind == "effects" and entry["duration"] > 60:
                        raise ValueError("Effect run exceeds60 seconds")
                    runs.append(entry)
                    beginning = None
            index = clip[kind].index(item)
            clip[kind][index:index + 1] = runs
    if len(clip["effects"]) > 32 or len(clip["materials"]) > 64:
        raise ValueError("Interval capacity exceeded")
    keys = sum(len(item["keys"]) for item in clip["morphs"])
    keys += sum(len(track["keys"]) for item in clip["decals"] for track in item["tracks"])
    if keys > 16384:
        raise ValueError("Total key capacity exceeded")
    encode(clip)
    return clip
