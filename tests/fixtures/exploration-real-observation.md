# Recorded-game fixture provenance

`exploration-real-observation.json.gz` is a JSON observation captured from a real game on 2026-10-21 at 14:21:44 Asia/Shanghai. It was extracted from local Trace `agent-trace-7b3930524ded482d915de4dd6544898e.jsonl`, sequence 41, generation 1 (the audit's primary sample).

Only the world/session/observation identifiers, corresponding entity instance key prefixes, and two player display names were replaced with neutral values. Tile layout, exact coordinates, shapes, liquids, hazards, player physics, entity flags, candidate anchors, timestamps and completeness are recorded data. No chat, API key or environment file is included. Gzip is only fixture storage; production exploration results are ordinary JSON.

The source is partial and has 8,470 cells, 46 entities and 329 frame-important candidates. It does not provide all liquid/shape/death/unloaded scenarios. Those cases in `test_exploration.py` are explicitly synthetic mutations, not claims about this recorded scene. The deidentification changes byte counts relative to the original tool result.

Run `python tests/measure_exploration.py` to reproduce a comparison with identical Python JSON serialization. This measures JSON objects rather than claiming an exact TCP or model Token cost.
