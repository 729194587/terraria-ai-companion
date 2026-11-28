# Autonomous Agent V1 — Codex Task Brief

## Goal

Implement a first end-to-end prototype of **Autonomous Mode** for Terraria AI Companion.

After the player explicitly enables autonomous mode, the Agent should observe the world, choose a useful next objective, execute a bounded action using existing game capabilities, inspect the result, and decide what to do next without requiring a new player chat message.

The prototype should demonstrate:

`enable → observe → decide → act → verify → observe → decide`

## Repository context

- Target branch: `codex/navigation-experiments`.
- Previous review baseline: `a24268ab`. First inspect the current checkout and use its actual `HEAD`; do not assume the branch has not advanced.
- The project already has a Python Agent, a C# tModLoader host, Lua tools, server-side actions/tasks, persistent map data, and native navigation. Extend these paths where practical.

Relevant entry points to inspect:

| Area | Starting points |
|---|---|
| Agent turn and lifecycle | `python/agent.py` |
| Action dispatch and authorization | `python/atomic_operations.py`, `python/server_ai.py` |
| Lua tool surface | `python/lua/semantic.lua`, `python/lua/explore.lua`, `python/lua_worker.py` |
| Existing goal/task handling | `python/player_goals.py`, `python/program_tasks.py` |
| Exploration projection | `python/exploration.py` |
| Chat and control bridge | `CompanionBridge/Common/Systems/ChatBridgeSystem.cs`, `CompanionBridge/ControlState.cs` |
| Server AI protocol and action boundary | `CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs`, `CompanionBridge/ServerIpc/AiAtomicActions.cs`, `CompanionBridge/ServerIpc/ServerAiNativeActions.cs` |
| Scene and map perception | `CompanionBridge/ServerIpc/ServerSceneReader.cs`, `CompanionBridge/ServerPlayerRuntime/AiMap.cs`, `CompanionBridge/ServerPlayerRuntime/AiMapRuntime.cs` |

Treat these as entry points, not a complete change list. Follow the actual call chains and repository conventions before editing.

## Agreed behavior

### 1. Player authorization

Provide runtime commands:

- `/agent autonomy on`
- `/agent autonomy off`
- `/agent autonomy status`

Authorization is session-scoped:
- It is off by default when entering a world.
- It is not stored in `ModConfig` or persisted across world sessions.
- Leaving the world or losing the bound world/actor identity turns it off.
- A future Mod UI may call the same controller, but UI work is out of scope for V1.

Enabling autonomy starts the first decision cycle without waiting for another player message.

Disabling autonomy prevents new autonomous work, cancels only work owned by autonomy, and requires confirmed input release before reporting that shutdown is complete.

### 2. Player control and death

Player instructions take priority:
- A new manual instruction pauses autonomous scheduling and yields control to the player.
- After the manual task reaches a known terminal state and input release is confirmed, autonomy may resume if it is still authorized.
- Resume only after taking a fresh observation; do not blindly continue the previous action.

Death does not revoke autonomous authorization:
- Stop the old body action and confirm input release.
- Preserve the autonomous objective and a concise progress summary.
- Wait for the existing verified respawn flow.
- Re-read the new life's state and world context, then let the LLM choose what to do.
- Never resume a navigation/action handle from the dead actor generation.

### 3. Decision and exploration

The LLM owns high-level objective selection. It should use the existing map and perception tools to decide:
- Which unexplored area or direction to investigate.
- Whether a nearby cave or other point of interest is worth visiting.
- Whether a newly discovered resource should change the current objective.
- Whether to continue, switch objectives, or stop for now.

Use `query_map` and current local perception as inputs. The existing Frontier implementation must not be the default strategic selector for Autonomous Mode. Keep existing Frontier functionality compatible; do not delete or broadly rewrite it in this task.

Use existing Navigation for movement and physical reachability. Exploration does not require every candidate to have a precomputed round trip to the starting anchor. Keep the safety checks that belong to actual movement execution.

### 4. Local world sensing

Extend the current sensing path rather than creating a parallel perception stack.

The V1 sensor should combine:
- Direct local scene observation.
- A bounded nearby resource scan that can report exact Tile coordinates for detected resources, including targets not directly visible through walls within the scan range.
- Historical map/POI information, clearly marked as historical rather than current fact.

Prioritize adding semantic detection for:
- Chests.
- Life crystals.
- Existing ore clusters and dropped items.

Return enough evidence for the Agent to reason about each finding:
- Resource kind and content/type identity.
- Exact Tile coordinate or object bounds.
- Observation source and game update/time.
- Coverage/completeness, truncation, and unknown information.
- Whether the result is newly observed, previously known, or needs revalidation.

A resource hit does not prove that it is reachable, collectible, or interactable. A nearby resource scan must not reveal the hidden terrain around the resource or mark those Tiles as explored in `AiMap`.

Keep scans bounded to the local area. Do not build a world-wide resource index in V1.

### 5. Autonomous action scope

Autonomy is broad permission to use the Agent's existing supported gameplay abilities, including exploration, resource gathering, ordinary combat, survival, crafting, inventory operations, and building.

Boss policy:
- The Agent must not initiate a Boss fight or deliberately trigger a Boss/progression event.
- It may gather preparation materials and build equipment or arenas.
- If a player or teammate starts a Boss fight, the Agent should recognize the active encounter and participate using existing combat capabilities.

Enforce the Boss initiation boundary at the real action/tool execution boundary, not only in the LLM prompt. Preserve ordinary item use and ordinary combat.

## Boundaries for this prototype

- No Mod UI.
- No persistent autonomy toggle in `ModConfig`.
- No Better Experience Mod integration or potion-specific perception feature.
- No global resource index or whole-world resource scan.
- No multi-agent redesign.
- No full GoalStore rewrite or independent GoalSteward-style evaluator.
- No broad Frontier deletion/refactor.
- No new navigation implementation.

Keep the first implementation focused on a working end-to-end loop. Prefer existing task receipts, action ownership, identity binding, cancellation, and input-release mechanisms.

## Acceptance criteria

1. `on`, `off`, and `status` commands work in-game and control one session-scoped runtime authorization state.
2. Enabling autonomy starts an LLM decision cycle without a new chat message.
3. The Agent can use the map and local sensor to select an unexplored area or a resource/POI target, navigate toward it, observe again, and make a new decision from the result.
4. The sensor reports chest and life-crystal findings with correct coordinates and explicit coverage/freshness; existing ore/drop sensing continues to work.
5. Repeated observations do not cause an unbounded stream of duplicate discoveries or LLM wakeups.
6. A manual player instruction preempts autonomous work; after the manual task completes, autonomy resumes only if still enabled and only after fresh observation.
7. Turning autonomy off stops new autonomous decisions, cancels only autonomy-owned work, and verifies input release.
8. Death preserves authorization/objective; verified respawn starts a fresh decision cycle and never resumes stale actions.
9. Autonomous actions cannot initiate Boss encounters, while an already-started team Boss encounter remains eligible for participation.
10. Relevant tests and build checks pass. Report any native multiplayer scenario that could not be run rather than claiming it passed.

## Verification

Use the repository's existing test and build scripts. First identify the smallest relevant suites for:
- Python Agent lifecycle and task scheduling.
- C# command/control and action admission.
- Lua/Python resource sensing.
- Death/respawn and manual takeover.
- Boss initiation policy.

Add regression tests for the new behavior. Run focused tests first, then the relevant build and native dedicated-server scenarios available in the environment.

For the end-to-end acceptance run, capture evidence for:
- Enabling autonomy with no subsequent player chat.
- At least one LLM-selected navigation target.
- A newly sensed resource changing the next decision.
- Manual takeover and safe resumption.
- Autonomy shutdown with input release.
- Death/respawn recovery.
- Boss initiation rejection and participation in an already active encounter.

Record commands, results, skipped checks, and performance observations. Do not report a test as passed unless it was actually run.

## Delivery

- Implement in reviewable milestones; keep commits logically separated.
- Provide a concise change summary and the key call chains changed.
- Report test/build commands and their actual results.
- Include native test evidence where available.
- Provide the final Git commit SHA.

## Codex workflow

This is a cross-cutting task. Start with `/plan` before editing.

In the planning pass:
1. Inspect the current branch, `HEAD`, applicable `AGENTS.md` files, and existing tests.
2. Trace the command path, autonomous action authorization path, task ownership/cancellation path, and respawn path.
3. Identify any conflicts between this brief and the current implementation.
4. Propose a short implementation plan with the files/modules likely to change and the focused tests for each milestone.

After reviewing the plan, implement the approved approach. Keep implementation choices grounded in the current code; adjust the plan when repository evidence requires it.

---

## OpenAI Codex prompting references

This task brief follows OpenAI's recommended Codex prompting approach: describe the desired behavior, provide relevant repository context and file entry points, state the few constraints that materially affect the result, and define how to verify the change. It is intentionally written in a GitHub-issue style rather than as a rigid implementation recipe.

- [Prompting Codex — OpenAI](https://learn.chatgpt.com/docs/prompting#prompting-codex)
- [How OpenAI Uses Codex — best practices](https://openai.com/business/learn/how-openai-uses-codex/)
