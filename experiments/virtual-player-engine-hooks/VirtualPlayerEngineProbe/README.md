The shared, validated G1–G7 C# runtime now lives in
`CompanionBridge/ServerPlayerRuntime` (namespace retained for compatibility).
`../build.ps1` stages these exact sources with `ProbeMod.cs` into the standalone
experimental package. Existing profiles and native self-tests remain available.
Do not build this wrapper folder directly with tML; use `../build.ps1`.
