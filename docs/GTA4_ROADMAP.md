# GTA IV expansion roadmap

GTA IV should be treated as a separate game backend, not as another conditional branch inside the San Andreas/III/VC implementations.

Plugin-SDK currently exposes a dedicated `plugin_IV` target and identifies the Complete Edition as `GAME_CE` / `PLUGIN_SGV_CE`. The official SDK also uses RAGE-oriented headers for this backend, which is materially different from the RenderWare-era trilogy targets.

## 1. Supported executable baseline

Phase 0 must freeze exactly which GTA IV executable is supported.

Recommended first target:

- GTA IV Complete Edition
- PC
- x86
- one known executable build only for the first milestone

The SDK currently identifies Complete Edition separately. It also contains legacy detection for older 1.0.0.4 and 1.0.0.7 executables, but those should be separate compatibility targets rather than silently sharing addresses.

Do not begin with a multi-version address abstraction. First make one version stable, then add version records.

## 2. Proposed source layout

```
src/
  gtaiv/
    effects/
      generic/
      player/
      vehicle/
      world/
      camera/
      hud/
    util/
      GameHandler.h/.cpp
      GameUtil.h/.cpp
      GameVersion.h/.cpp
      HookDatabase.h/.cpp
      Memory.h/.cpp
      Renderer.h/.cpp
```

The shared code should remain platform-neutral.

## 3. New CMake target

Eventually add:

- `PluginSDK::gtaiv`
- `cmake/gtaIV.cmake`
- `TrilogyChaosMod.IV.asi`

The current Plugin-SDK Find module in this fork only declares III, VC and SA components, so GTA IV support will require a first-class `gtaiv` component there as part of the IV work.

The build must remain x86 and must pin one verified Plugin-SDK revision for reproducible binaries.

## 4. Runtime architecture

GTA IV should implement the same logical interfaces already expected by the shared engine:

- `GameUtil::IsCutsceneProcessing()`
- `GameUtil::IsPlayerSafe()`
- game initialization and per-frame processing
- game-specific player/vehicle/streaming helpers
- game-specific rendering hooks

The important change is to keep the effect lifecycle identical while isolating RAGE memory access behind small game-specific helpers.

## 5. Hook strategy

Do not start by copying San Andreas absolute addresses.

Create a version-scoped address table:

```
struct GameAddressSet {
    uintptr_t playerGetter;
    uintptr_t processHook;
    uintptr_t drawHook;
    uintptr_t timerHook;
    ...
};
```

Resolve the selected set once during initialization.

All hooks must have:

- a documented purpose
- the executable version they target
- the original instruction bytes recorded during development
- a cleanup path
- a failure path that leaves the game untouched

Where practical, prefer stable SDK wrappers or pattern/signature resolution over scattered magic constants.

## 6. Renderer

Do not reuse RenderWare assumptions from III/VC/SA.

GTA IV uses a RAGE-based stack. The first renderer milestone should therefore provide only:

- top cooldown bar
- three-way vote display
- active-effect list
- simple text
- effect timers

Only after those are stable should more advanced HUD effects be ported.

The existing websocket protocol should remain the same. The GTA IV backend should consume the same `time`, `votes` and `effect` messages.

## 7. Effect portability matrix

Every effect should be classified before porting:

| Class | Examples of work | Priority |
|---|---|---|
| Shared logic | timers, random selection, metadata | P0 |
| Low-risk game APIs | weather, time scale, simple camera changes | P1 |
| Player state | movement, wanted state, health/state manipulation | P1 |
| Vehicles | spawn, handling, physics | P2 |
| World/streaming | teleport, population, streaming changes | P2 |
| Renderer-specific | custom HUD/post-process effects | P3 |
| Mission/save hooks | save/load, mission state | P3 |
| High-risk memory effects | deep engine hooks | P4 |

The goal is feature parity by category, not one enormous port.

## 8. GUI compatibility

The GUI should detect:

```
type = "game"
data.id = "gta4"
```

and use an effect manifest filtered by game capability.

An effect should be activatable only when the backend advertises the capability it needs. That avoids the current problem where a GUI can know an effect name but the game plugin has no implementation for it.

Recommended capability names:

- `weather`
- `time_scale`
- `player_state`
- `vehicle_spawn`
- `teleport`
- `camera`
- `hud`
- `mission`
- `save_system`

## 9. Versioning

The mod protocol version and game backend version should be independent.

For example:

- protocol: 2
- game: `gta4`
- backend: `iv-ce`
- backend version: `0.1.0`

This lets the GUI support multiple game builds without changing the message format.

## 10. Testing plan

Before calling GTA IV supported:

### Boot
- clean install
- ASI loader detection
- no duplicate plugin load
- clean shutdown

### UI
- game handshake
- cooldown bar
- vote rendering
- active effects
- menu/pause transitions
- resolution/aspect changes

### State
- new game
- load game
- mission
- cutscene
- death/arrest states
- interior/exterior transitions

### Effects
- one-time effect
- timed effect
- conflicting effects
- effect cleanup
- invalid effect data
- websocket reconnect
- delayed websocket reconnect

### Soak
Run long streams with repeated effects and monitor:

- crashes
- hook failures
- leaked objects
- timers
- stale effects
- rendering corruption
- websocket state

## 11. Suggested milestones

### M0 — Research lock
Freeze executable, SDK revision, loader and supported language/build.

### M1 — Backend skeleton
Boot, version detection, GameUtil, hooks, websocket handshake.

### M2 — Renderer
Port the existing cooldown/voting/effect UI only.

### M3 — First effects
Weather, time scale, basic player state, basic camera.

### M4 — World and vehicles
Teleport, vehicle spawning and population changes.

### M5 — Mission/save integration
Only after the earlier layers survive long soak tests.

### M6 — Effect parity
Port effects category by category and track unsupported capabilities explicitly.

### M7 — Multi-version support
Add another executable only after the first target is stable.

## 12. Main technical risks

1. RAGE internal structures are substantially different from the RenderWare trilogy.
2. A working hook on one GTA IV executable cannot be assumed valid on another.
3. UI rendering and post-processing effects are more engine-specific.
4. Mission/save state has much higher crash risk than simple effects.
5. Multi-version support can multiply the testing matrix quickly.

The project should therefore ship GTA IV as an explicitly experimental backend first, with a small stable effect set, rather than claiming complete parity from the first release.
