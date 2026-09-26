# Open Swing runtime replay

The playground now has a Unity batch replay that runs the public Open Swing
reference and the independent `HairDynamicsSystem` on the same invented
transform chain. It is a comparison harness, not a claim of equivalence.

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
reported three simulated nodes; the independent solver reported three dynamic
entries and two simulated segments. The measured maximum tip-position delta was
`0.4555227`, so `equivalent` remains `false` and the gap is intentionally
visible to later work.
