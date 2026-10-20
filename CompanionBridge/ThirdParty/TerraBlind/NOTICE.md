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

- `Nav/StateSpacePlanner.cs`: adapted `CellKey`, `Label`, `Dominated`,
  `BuildHoldOptions`, and the `Plan` frontier/labels/came-from expansion, grounded
  partial-result fallback and reverse action reconstruction into
  `Motion/StateSpacePlanner.cs`. Replaced Terraria/global field calls with explicit
  state/parameters/IMotionWorld. The expansion uses our version-corrected predictor,
  validates braking on every edge and retains its safe-landing/drop restrictions.
  Since 0.1.12 walking may execute only the moving prefix, retaining its endpoint
  velocity in search labels. Since 0.1.13 navigation jumps can also hand off after
  two supported, jump-released landing frames, retaining the extra grounded step
  used by upstream SimulateSegment. A full braking tail is still validated, and
  final approaches or unsupported moving handoffs settle. Legacy single jumps
  keep their settled contract. Short walk
  approaches, a jump preference cost and cost-aware partial selection reduce
  unnecessary jumps without changing the physics core.
  Since 0.1.14 navigation uses a separate bounded 64px descent limit so routes
  can leave two-tile-high obstacles; legacy single-action prediction keeps its
  original 16px limit. Full landing/braking validation remains mandatory.
  Since 0.1.16 rolling-jump support frames also require near-zero downward
  velocity: native clipped contact can precede TerrainReader's grounded state.
  Search is bounded to 32 expansions / 25ms between candidates, a 128px rolling
  horizontal goal and the existing local world window. Horizontal crossing goals
  replace stand-cell XY goals; maze heuristics, digging, placement, cache/profiler,
  background planning and global routing are not ported. This is an adapted subset,
  not a drop-in copy of the complete upstream navigation system.

Reference only (not copied wholesale): `Actions/JumpExecutor.cs` input enumeration
and landing feedback; `Perception/PhysicsRecorder.cs` frame recording;
`Nav/RecedingNav.cs` plan/dispatch/verify/replan lifecycle; `Nav/ActionGraphPlanner.cs`
action graph comparison. `Motion/NavigationTask.cs` is the Bridge-specific lifecycle
adapter. Existing `LocalMotionController` replays predicted inputs at the installed
input hook and compares actual post-update states; upstream `ReplaySystem` and
its hook timing / fake initial velocity are not imported. No PathPlanner,
DragonLens, HTTP server, world modification, or LLM dependency is imported.
