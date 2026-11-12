# Terraria Scenario Benchmark Phase 1

Run from the repository root in PowerShell. The existing native harness copies the seed world and matching mod world, starts a dedicated server on the supplied ports, and tears it down after each attempt.

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/benchmark-build
& .local/lua-venv/Scripts/python.exe -B tests/benchmark/runner.py run --package .local/benchmark-build/Mods/CompanionBridge.tmod --world .local/virtual-player-control-host/Worlds/test.wld --repeat 2
```

Use `--scenario ore_cluster_approach`, `--variant solution` or `--variant noop` to narrow the run. `--output` selects the parent output directory. `--port` and `--ipc-port` select two distinct unused ports. `--engine-profile` and `--tml-path` select a pinned engine installation.

## Scenario manifest

[scenarios.json](../tests/benchmark/scenarios.json) registers the three Phase 1 scenarios. Each record contains its ID, description, natural-language opening, sibling Lua file, target offsets, and check thresholds. Shared setup defines the arena extents relative to the actor’s initial floor tile, terrain and ore types, and loadout. The manifest also sets Lua budgets and metric names. The Solution script discovers the copper cluster through `observe_scene`, executes `mine_target` with `cluster=true`, and observes again.

## Evidence and evaluation

The runner delegates server creation to [verify_server_ai_native.ps1](../tests/verify_server_ai_native.ps1). The marked fixture runs on the game thread after native updates. It constructs the arena, records its initial fingerprint and native reach, and tracks real mining proofs and navigation completions for the bound actor. A separate evaluation request records the final world. The two atomic file exchanges match the run, scenario, phase, nonce, and binding.

The evaluator applies the same objective to both variants. It compares the exact target cells to native removal proofs and checks the scenario’s navigation and stance requirements. Guards independently check life, input release, and a digest of all non-target terrain in the arena. The digest covers tile occupancy, material, shape, wall, liquid, wires, and actuation. Ordinary tile reframing does not affect it. The evaluator also checks Lua execution and terminal receipts. A No-op passes when the objective remains false, targets remain intact, and all guards and No-op execution checks pass.

Each invocation writes `summary.md`, `runs.jsonl`, full attempt JSON, Lua evidence, and scoped native logs under `.local/benchmark/runs/<run-id>/`. Initial fingerprints exclude binding and update counters. Source world hashes are checked after each attempt. Report statuses distinguish scenario failure, setup error, and evaluation error. `wallSeconds` measures Lua execution and `harnessWallSeconds` includes server startup and teardown.

Phase 1 covers these three dry, bounded copper scenarios on an isolated dedicated server. Solution is a scripted baseline. The fixture protocol and evaluator can be reused by a future executor, including a Live Agent. Graphical multiplayer, external models, and collection into inventory are outside these checks.
