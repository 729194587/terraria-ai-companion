# TerraBlind attribution and scope

Source: https://github.com/Reisenbug/TerraBlind

Pinned commit: `26361f488da986ee8739dded76fabedfa04f6aa8`.
Upstream `build.txt` identifies author **Stardust**; GitHub owner is **Reisenbug**.
The README at this commit explicitly declares **MIT**. No standalone LICENSE or
copyright header was supplied in that checkout. Its MIT declaration is retained
here; `LICENSE` supplies the standard MIT permission text without inventing an
upstream copyright year.

Adapted code:

- `Perception/PhysicsSimulator.cs`: dry horizontal/jump step, bounded trajectory
  iteration, native `WalkDownSlope → StepDown → StepUp → TileCollision →
  SlopeCollision` sequence. Local files: `Motion/PhysicsSimulator.cs` and
  `Motion/NativeMotionWorld.cs`.
- Water/honey, static baked-parameter caches, extra jumps, placement, and global
  navigation are excluded. Read finalized live parameters at our existing
  PostUpdateRunSpeeds input hook instead.
- Horizontal else-if ordering is confirmed against the installed IL. The launch
  counter and release eligibility are adapted to
  our installed 1.4.4.9 IL; upstream comments citing 1.4.5.4 are not evidence
  of compatibility. Speculative collision flags are saved/restored.
- Dry max-fall uses the effective 10.01 value: Player.Update adds 0.01 to
  its reset value 10 before the input hook. Confirmed by local IL and user feedback.

Reference only (not copied wholesale): `Actions/JumpExecutor.cs` input enumeration
and landing feedback; `Perception/PhysicsRecorder.cs` frame comparison;
`Nav/StateSpacePlanner.cs` physics-state expansion. The local lease-bound executor
is specific to CompanionBridge. No ReplaySystem, PathPlanner, DragonLens, HTTP
server, world modification, or LLM dependency is imported.
