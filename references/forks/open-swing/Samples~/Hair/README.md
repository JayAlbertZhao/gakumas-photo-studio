# Swing solver: hair integration

This package contains source code and synthetic examples only. It includes no character model, animation, texture, extracted collision configuration, or scene asset.

## Files

- `Runtime/Hair/ActorAnimationSwingSolver.cs`: transform-chain integration, constraints, wind, and collision solver shared by hair and cloth.
- `Runtime/Hair/FixedStepSwingClock.cs`: optional fixed-step input sampling and output interpolation.
- `Runtime/Hair/HairSwingAdapter.cs`: Unity component that binds a user-owned hierarchy and JSON configuration.
- `Samples~/Hair/ExampleChain.json`: synthetic schema example for a `JointA -> JointB -> Tip` hierarchy; it does not describe a real character.

## Setup

Use Unity with the `com.unity.mathematics` package. Create a `TextAsset` from a configuration matching your own transform names. Assign it and the animated hierarchy to `HairSwingAdapter`. For a synthetic test, create a root with `JointA`, its child `JointB`, and a final child `Tip`, each with nonzero local offsets, then assign `ExampleChain.json`.

The adapter restores simulated local poses in `Update` and advances the solver in `LateUpdate`. If the host samples animation manually, disable `automaticUpdate` and call `RestoreBeforeAnimation()` before sampling, then `SimulateAfterAnimation(deltaTime)` after the body pose and collision anchors are final. The optional fixed-step clock is a presentation policy; `ActorAnimationSwingSolver.NativeStep` is the internal integration increment.

Provide `motionSource` explicitly if the visual root does not represent root motion. `head` and `neck` are only needed when the configuration uses head or neck driven secondary roots. Supply `explicitBindings` if the hierarchy contains duplicate transform names. A simulated joint must have a child joint with a nonzero local offset.

For a segment from parent joint A to child joint B, the solver reads dynamic coefficients from B's node entry when present, or falls back to A's entry. A's entry supplies limits for that segment. A terminal child entry can therefore supply the last segment's dynamics even though it has no child of its own.

The adapter checks for an empty binding result and requests a solver reset after large root moves. The numerical behavior of the core solver depends on its configuration and was not validated against arbitrary third-party rigs.
