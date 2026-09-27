# Open Swing runtime replay

The playground has a Unity batch replay that runs the public Open Swing
reference and the independent `HairDynamicsSystem` on the same invented
transform chain. The checked-in fixture now reaches exact synthetic dynamics
parity; policy differences remain reported separately.

## Run

Use a workspace on a drive with enough space for Unity's generated `Library`
folder. The runner copies source and synthetic configuration only:

```powershell
$unityEditor = Read-Host "Path to Unity 2022.3.57f1 editor"
$workspace = Read-Host "Workspace path on a non-system drive"
python -I tools/run_open_swing_runtime_replay.py `
  --unity-editor $unityEditor `
  --workspace $workspace
```

The probe executes eight explicit steps at `0.01667` seconds with wind
disabled. A successful log contains `RUNTIME_DYNAMICS_REPLAY_OK`. Unity may
emit a local license-client warning in an unlicensed batch environment; this
does not change the synthetic editor replay result, and the harness never
builds or includes a player license.

## Current receipt

The checked-in [runtime comparison receipt](../references/hooks/open-swing-runtime-comparison-receipt.json)
records the latest run. Both solvers produced finite output. The reference
reported three simulated nodes; the independent solver reports three dynamic
entries and three simulated segments after enabling the opt-in terminal
transform proxy. The latest eight-step replay has per-edge tip deltas of
`0`, `0`, and `0`, with `equivalent=true` for the synthetic dynamics scope.
The receipt fixes the reference-half-pass prewarm, deferred reset boundary, and
an empty static-collider fixture (`head`/`chest` are intentionally null).
The receipt still records a deliberate policy difference: an invalid capsule
axis throws in the reference helper while the independent solver clamps it
by default; the new opt-in
`useReferenceCapsuleAxisValidation` path throws with the reference rule.
The same fixture also evaluates the reference `SkirtRootMath` and the
independent skirt helper; the current synthetic case has zero angular delta.
The probe also resets both solvers through their public reset boundaries and
checks finite output after the reset replay.
