"""Optional Maya authoring bridge. Actual Maya host execution is not yet validated.

Add this directory to sys.path inside Maya. Explicit controls share one keying panel;
no scene-wide discovery, arbitrary script evaluation, network or asset download.
"""
from pathlib import Path
from performance_authoring import PROPERTY, bake, encode


def export(controller, template, mappings, start_frame, end_frame, fps, destination=None):
    import maya.cmds as cmds
    path = Path(destination) if destination is not None else None
    if path is not None and path.suffix != ".performance":
        raise ValueError("Sidecar extension must be .performance")
    clip = bake(template, mappings, start_frame, end_frame, fps,
                lambda attribute, frame: cmds.getAttr(attribute, time=frame))
    payload = encode(clip)
    if not cmds.attributeQuery(PROPERTY, node=controller, exists=True):
        cmds.addAttr(controller, longName=PROPERTY, dataType="string")
    cmds.setAttr(controller + "." + PROPERTY, payload, type="string")
    if path is not None:
        path.write_text(payload + "\n", encoding="utf-8")
    return clip


def show_editor(mappings):
    """Edit/key existing explicit numeric attributes. Blendshape/decal/effect controls share Maya's graph editor."""
    import maya.cmds as cmds
    window = "photoStudioPerformanceEditor"
    if cmds.window(window, exists=True):
        cmds.deleteUI(window)
    cmds.window(window, title="Character Toolkit Performance", widthHeight=(460, 480))
    cmds.scrollLayout()
    cmds.columnLayout(adjustableColumn=True)
    for mapping in mappings:
        attribute = mapping["attribute"]
        if not cmds.objExists(attribute):
            raise ValueError("Missing explicit numeric attribute: " + attribute)
        cmds.rowLayout(numberOfColumns=3, adjustableColumn=2)
        cmds.text(label=mapping["kind"] + ": " + mapping["target"])
        cmds.floatField(value=cmds.getAttr(attribute), changeCommand=lambda value, a=attribute: cmds.setAttr(a, value))
        cmds.button(label="Key", command=lambda unused, a=attribute: cmds.setKeyframe(a))
        cmds.setParent("..")
    cmds.showWindow(window)
    return window
