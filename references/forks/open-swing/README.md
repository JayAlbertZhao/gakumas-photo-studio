# Open Swing Dynamics

A code-only Unity package for driving hair and skirt transform chains. It provides a shared swing solver, a fixed-step clock for hair, and a skirt-root driver. The example configuration uses invented bone names and illustrative values.

This repository contains no character models, meshes, textures, animations, audio, game files, or extracted character configuration. Bring your own rig, animation, and tuning data. The sample is a schema guide, not a finished character preset.

## Requirements and installation

- Unity 2022.3 or later.
- Unity Mathematics, declared as a package dependency.

Add this repository through Unity Package Manager using **Add package from git URL**:

```text
https://github.com/Arin3000/unity-hair-skirt-dynamics.git
```

You can also copy the `Runtime` source into a Unity project with Unity Mathematics installed.

## Components

| Component | Role |
| --- | --- |
| `ActorAnimationSwingSolver` | Runs transform-chain swing, collision, smoothing, and optional hair-root drivers. |
| `FixedStepSwingClock` | Advances a manually controlled solver at a fixed tick and blends display poses for hair and skirt rigs. |
| `SkirtRootMath` | Converts a reference joint rotation into a skirt root rotation. |
| `SkirtMotionController` | Binds a user-supplied skirt rig and applies root drivers before fixed-tick chain dynamics. |

The caller owns the animation sampling loop. After sampling a pose, synchronize any separate clothing bones, run skirt motion, then run hair motion. Reset a solver after teleporting or replacing the rig. The adapters deliberately do not load assets or find a character automatically.

See the files in `Samples~` for synthetic configuration and binding examples. A configured chain needs a child transform with a nonzero local offset, and configured bone names must be unique within its rig. Tune coefficients and colliders for your own geometry.

## Release check

Run `python tools/check_release.py` before publishing changes. It rejects character/media file types, unexpected files, local paths, credentials, email addresses, and revision-style labels. Review names and sample data manually as well; a text scan cannot establish ownership of assets or the suitability of rig parameters.

## License

MIT for the source code in this repository. Third-party rigs, animations, and assets are not included or licensed here.
