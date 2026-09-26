# Skirt motion adapter

This package contains a skirt root rotation function, a small adapter for the shared `OpenSwing.ActorAnimationSwingSolver`, and a synthetic JSON example. It contains no mesh, texture, animation, extracted game configuration, or character-specific parameter set.

## Dependencies

- Unity and the `Unity.Mathematics` package.
- `Runtime/Hair/ActorAnimationSwingSolver.cs` from the same package.
- A rig and animation supplied by the application using this code.

## Configuration

The adapter and the solver read the same JSON `TextAsset`:

| Field | Consumer | Purpose |
| --- | --- | --- |
| `drivers` | `SkirtMotionController` | Reference joint to skirt root rotation. `rotationOrder` supports only `0` (XYZ). |
| `nodes` | Shared swing solver | Dynamic bones. A node needs a child with a nonzero local offset. |
| `chains` | Shared swing solver | Optional links between segments at the same depth. `bones` names their child endpoints. Closed loops (`around: true`, at least three segments) can use smoothing. |
| `staticColliders` | Shared swing solver | Optional primitive colliders, bound by anchor name. |

`SyntheticSkirt.json` is an illustrative schema example with invented names and values. It assumes this hierarchy:

```text
RigRoot
└── Pelvis
    ├── LeftUpperLeg
    └── SkirtPanelRoot
        └── SkirtPanelMid
            └── SkirtPanelTip
```

Supply your own rig, rest rotations, joint names, coefficients, dynamic settings, and colliders. Each segment reads its dynamic coefficients from the child endpoint's node entry when present, or falls back to the parent entry; its limits come from the parent entry. The sample values are not tuned for any production mesh. It leaves `chains` empty because a single branch has no same-depth neighbors. Every bone name in the configured branch must be unique.

## Scheduling

Create a `SkirtMotionController` on any persistent GameObject, then bind after instantiating the clothing rig:

```csharp
skirt.Bind(clothingRig.transform, skirtConfig, pelvis);
```

For each frame, call the methods in this order:

```csharp
skirt.RestorePose();              // Before animation sampling.
SampleAnimationPose();            // Your animator or sampling code.
SynchronizeClothingBones();       // If the clothing rig is separate.
skirt.Step(Time.deltaTime);       // Root drivers, then fixed-tick dynamics.
StepHairIfPresent();              // Optional: after clothing.
```

Call `skirt.RequestReset()` after a teleport and `skirt.Release()` before destroying or replacing the clothing rig. For a rig with only root drivers, call `Bind(..., enableDynamics: false)`.

The adapter performs no animation sampling or asset loading. It fails if a configured bone is missing or if no dynamic segment binds while dynamics are enabled, so a mistaken configuration does not appear to run successfully.
