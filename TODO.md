# TODO: cheaper chunk visibility pass

Found while building the Rendering Addon (2026-09-29, yard idle, RTX 3070).

## Problem
`ChunkManager.ProcessChunkVisibility` runs every frame (from `CinemachineBrain.LateUpdate`) and costs ~1.8 ms:
- ~1.2 ms game work: for every static layer it walks all objects of every visible/prewarm chunk (~3100 StaticObjects + ~570 StaticWgo), calls `GetChunkableData` on each, builds HashSets, and re-dispatches `DispatchChunkVisibilityState` for **every** visible/prewarm object (~1850 calls/frame) even when nothing changed.
- ~0.45 ms from this mod: `DispatchChunkVisibilityStatePatch` prefix/postfix runs on each of those ~1850 calls.

## Fix
1. Only call the per-object logic (`PrewarmThrottle.Resolve` / `ChunkStreaming.Resolve`) when an object's state changed since its last dispatch, or while it is deferred by the budget (keep a small "pending" set so deferred objects retry next frame). Unchanged Visible/Prewarm objects should return early before any work.
2. Optionally skip whole static-layer recomputation when frustum planes, band padding and chunk membership are unchanged since last frame (idle camera); still process the dynamic layer.
3. Keep the dispatch order and results identical; verify with the harness (frozen `timescale 0` screenshots + `countm ChunkManager.DispatchChunkVisibilityState`).

Note: the Rendering Addon already replaces the tiny `Schedule().Complete()` jobs in this method with inline `Run` (transpiler, ~0.2 ms). Don't patch the same IL; prefixes/postfixes are fine.
