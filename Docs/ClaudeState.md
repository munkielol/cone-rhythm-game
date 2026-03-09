# Claude State Checkpoint — RhythmicFlow v0

**Date:** 2026-03-09
**Branch:** `nightly/claude-v0`
**Base branch for PRs:** `main`

---

## Last 15 commits

| Hash | Message |
|---|---|
| `7932fb5` | Update specs for judgement feel and flick arming toggles |
| `b171d4c` | Add judgement feel and flick arming toggles |
| `e059000` | Update test chart to test flick notes |
| `755d453` | Render debug arcs and lane lines on frustum surface |
| `33535f9` | Add debug arena frustum surface mesh |
| `a399bd6` | Add more lanes + notes to test chart for testing |
| `b9f04bb` | Add debug note approach visualization |
| `8536603` | Fix stringcomparer not exist |
| `00630bf` | Add PlayerDebugRenderer for arena/lane/note visualization |
| `a7ea86a` | Add test song rpk |
| `7be2d3b` | Add editor DevPacks override for pack loading |
| `979d121` | Add editor DevPacks override for pack loading |
| `5896278` | Add minimal Player app controller harness |
| `a5bb8cc` | Unity-generated meta files |
| `7964f90` | Add flick gesture tracking and judgement |

---

## Implemented systems summary

### Shared (`Assets/_Project/Shared/`)

| File | Purpose |
|---|---|
| `ChartJsonV1/ChartJsonV1.cs` | Top-level chart data model (formatVersion, song, tempo, arenas, lanes, camera, notes) |
| `ChartJsonV1/ChartArena.cs` | Arena keyframe tracks |
| `ChartJsonV1/ChartLane.cs` | Lane keyframe tracks |
| `ChartJsonV1/ChartNote.cs` | Note model + `NoteType` / `FlickDirection` string constants |
| `ChartJsonV1/ChartTrack.cs` | Generic keyframe track (value + easing) |
| `ChartJsonV1/ChartSong.cs` | Song metadata block inside chart JSON |
| `ChartJsonV1/ChartCamera.cs` | Camera keyframe tracks |
| `IO/ChartJsonReader.cs` | JSON → `ChartJsonV1` via `JsonUtility` |
| `IO/RpkReader.cs` | Read text/binary entries from `.rpk` (ZIP) |
| `Validation/ChartValidator.cs` | Full chart validation (tempo, tracks, keyframes, notes) |
| `Validation/ChartValidationResult.cs` | Validation result + error list |
| `Validation/ChartValidatorRunner.cs` | Runner wrapper for headless validation |
| `ChartDebugSummary.cs` | Debug string summary for loaded chart |

### Player (`Assets/_Project/Player/`)

| File | Purpose |
|---|---|
| `App/PlayerAppController.cs` | Main MonoBehaviour harness: scan packs → load audio → conduct → judge each frame |
| `Catalog/PackScanner.cs` | Scans a directory for `.rpk` files, validates each, builds `PackCatalog` |
| `Catalog/PackCatalog.cs` | In-memory list of validated `PackEntry` objects |
| `Playfield/PlayfieldTransform.cs` | Normalized ↔ local coordinate conversion; exposes `MinDimLocal`, `NormRadiusToLocal` |
| `Playfield/ArenaHitTester.cs` | Wrap-safe arena band + lane membership test (spec §5.5) |
| `Playfield/AngleUtil.cs` | `Normalize360`, `ShortestSignedAngleDelta` helpers |
| `Conductor/Conductor.cs` | DSP-time conductor; computes `EffectiveChartTimeMs` |
| `Gameplay/RuntimeNote.cs` | Mutable note runtime state (State, HoldBind, BoundTouchId, …) |
| `Gameplay/NoteScheduler.cs` | Chart notes → `RuntimeNote[]`; cursor-based active-window query; hold-tick sweep |
| `Gameplay/FlickGestureTracker.cs` | Per-touch gesture state (dist/vel/elapsed); pooled; `ResetGesture` for free-touch mode |
| `Gameplay/JudgementWindows.cs` | Mode-specific timing windows; `Evaluate()` with optional `perfectCoversGreat` |
| `Gameplay/JudgementEngine.cs` | Tap/Flick/Catch/Hold judgement + arbitration (spec §7.6); debug logging; arming sets |
| `Settings/PlayerSettingsStore.cs` | PlayerPrefs-backed settings + v0 debug toggle fields |
| `Debug/PlayerDebugRenderer.cs` | LineRenderer overlay: arena arcs, lane rays, note approach diamonds, flick arrows, touch marker |
| `Debug/PlayerDebugArenaSurface.cs` | Runtime mesh per arena band (ring-sector, cone/frustum Z profile) |
| `PlayerBoot.cs` | Scene entry point MonoBehaviour |

### ChartEditorApp (`Assets/_Project/ChartEditorApp/`)

| File | Purpose |
|---|---|
| `Runtime/Project/EditorProject.cs` | In-memory editor project model |
| `Runtime/UndoRedo/UndoStack.cs` | Generic undo/redo stack |
| `Runtime/Export/RpkExporter.cs` | Project → `.rpk` export pipeline |
| `ChartEditorBoot.cs` | Scene entry point MonoBehaviour |

---

## Flick direction semantics (locked, v0)

Directions are **lane-relative in the player-facing-inward frame**. Given lane center angle **θ** (degrees, 0° = +X, CCW positive):

```
radialOut     = ( cos θ,  sin θ)   outward from arena center
radialIn      = (-cos θ, -sin θ)   inward toward arena center
tangentialCCW = (-sin θ,  cos θ)   counter-clockwise around arena
tangentialCW  = ( sin θ, -cos θ)   clockwise around arena
```

| Chart `direction` | Maps to | Meaning |
|---|---|---|
| `U` | `radialIn` | toward arena center ("up" when facing inward) |
| `D` | `radialOut` | away from arena center ("down" when facing inward) |
| `L` | `tangentialCW` | clockwise ("left" when facing inward) |
| `R` | `tangentialCCW` | counter-clockwise ("right" when facing inward) |

Match threshold: `dot(normalised_displacement, expected) >= cos(45°) ≈ 0.707`.

Implemented in: `JudgementEngine.IsFlickDirectionMatch` and `JudgementEngine.DebugFlickExpectedDir`.
Visual arrows use `DebugFlickExpectedDir` so they always stay in sync with the judgement mapping.

---

## v0 debug/playtest toggles

Both are **plain `public static bool` fields** in `PlayerSettingsStore` — not PlayerPrefs-persisted, set in code or a debug Inspector.

### `PerfectWindowCoversGreatWindow` (default: `false`)

- When `false`: normal Standard/Challenger windows (Perfect 30/22 ms, Great 90/60 ms).
- When `true`: effective Perfect window expands to `GreatWindowMs`; **Great tier is suppressed**. Every in-window hit becomes Perfect (or Perfect+ if within `PerfectPlusWindowMs`). Perfect+ sub-window is **not** enlarged.
- Applied at: `JudgementEngine.TryJudgeTap` and `TryBindHold` — passed as `perfectCoversGreat` arg to `_windows.Evaluate()`.
- Catch and Flick are always Perfect-or-Miss; toggle has no effect on them.

### `FlickRequireTouchBegin` (default: `true`)

- When `true`: `TryJudgeFlick` returns `false` immediately unless `touch.IsNew`. Only new touches can arm a flick.
- When `false`: any active touch can arm a flick note. On first frame a touch is both inside the lane **and** inside `±GreatWindowMs` for a specific note, `ResetGesture` is called to set the gesture baseline to that moment (so `ElapsedMs` doesn't expire from a long hold). Arm state per `(noteId, touchId)` is tracked in `JudgementEngine._flickArmedSet`; cleared when note leaves the window or is judged.

---

## Pack location behavior

### In-editor (Unity Editor)

`PlayerAppController.useEditorProjectRootPacks = true` (default) redirects the scan to:
```
<project-root>/DevPacks/
```
i.e. `Application.dataPath + "/../DevPacks"`. Any `.rpk` file at the top level of that folder is loaded.
Currently: `DevPacks/TestSong.rpk` is the only file there.

### In builds (iOS/Android)

Scans `Application.persistentDataPath/Packs/`. Directory is created if missing. User must manually copy `.rpk` files there (v0 — no in-app importer).

---

## Test pack: `DevPacks/TestSong.rpk`

**Song ID:** `test_song` | **Difficulty:** `standard` | **BPM:** 120 constant | **Audio offset:** 0

### Arena

| Field | Value |
|---|---|
| arenaId | `arena-a` |
| centerX / centerY | 0.5 / 0.5 (screen center) |
| outerRadius | 0.45 (normalized) |
| bandThickness | 0.12 (normalized) |
| arcStartDeg | 190° |
| arcSweepDeg | 160° (arc from 190° to 350°) |

### Lanes (all static, `widthDeg = 30`, `arenaId = arena-a`)

| laneId | centerDeg | priority |
|---|---|---|
| `lane-0` | 210° | 0 |
| `lane-1` | 250° | 1 |
| `lane-2` | 290° | 2 |
| `lane-3` | 330° | 3 |

### Notes

| noteId | lane | type | timeMs | direction |
|---|---|---|---|---|
| `n1` | lane-0 | tap | 2000 | — |
| `n2` | lane-1 | tap | 2500 | — |
| `n3` | lane-2 | tap | 3000 | — |
| `n4` | lane-3 | tap | 3500 | — |
| `f1` | lane-1 | flick | 6000 | U (radial-in) |
| `f2` | lane-1 | flick | 7000 | D (radial-out) |
| `f3` | lane-1 | flick | 8000 | L (CW tangent) |
| `f4` | lane-1 | flick | 9000 | R (CCW tangent) |

### Known issues / caveats

- **No hold or catch notes** in the test chart yet; `TryJudgeFlick`/`TryJudgeCatch`/`TryBindHold` are code-complete but untested with real chart data.
- **Single arena only.** Multi-arena charts have not been validated in-game (the `_laneToArena` F2-fix in `JudgementEngine` was built for it but is untested).
- **`FlickRequireTouchBegin = true` by default.** With the current test chart + mouse input, the mouse-drag gesture needs to start on the frame the mouse button first goes down; holding the button and then moving may miss the window depending on timing.
- **No scene wiring documented.** `PlayerAppController`, `PlayerDebugRenderer`, and `PlayerDebugArenaSurface` must be manually wired in the Inspector; no prefab or scene YAML has been committed.
- **`PlayerAppController` uses first-keyframe geometry only** for the debug getters (`DebugArenaGeometries`, etc.) — animated arenas are not reflected in the debug overlay at runtime.
- **`DebugLogFlick` is currently `true`** in `JudgementEngine` (was set to true for active debugging). Set to `false` to silence per-frame gesture logs.
- **No scoring accumulation** is displayed yet; only the most recent `JudgementRecord` is shown in the `OnGUI` overlay.

---

## Next 10 concrete steps (priority order)

1. **Wire and smoke-test the scene in Unity Editor.**
   Confirm `PlayerAppController` loads `TestSong.rpk`, audio plays, arenas/lanes render via `PlayerDebugRenderer`, and tap notes at 2–3.5 s register in `OnGUI`. This validates the full boot→judge loop end-to-end before any new code.

2. **Verify flick direction arrows vs actual judgement.**
   With `DebugLogFlick = true` and `FlickRequireTouchBegin = false` (easier to test with mouse), perform swipes in each of the four directions on `f1–f4`. Confirm the green debug arrows match what passes/fails in the console log. Fix any remaining direction mismatch.

3. **Add hold + catch notes to the test chart.**
   Extend `DevPacks/TestSong/charts/standard.json` with at least one hold (with `tickTimesMs`) and one catch note to exercise `TryBindHold`, hold-tick evaluation, and `TryJudgeCatch`. Keep them simple and well-spaced.

4. **Add animated keyframes to the test arena.**
   Add a second keyframe to `centerDeg` or `arcStartDeg` so the arena moves during the song. This will reveal whether keyframe interpolation is wired correctly into `PlayerAppController`'s per-frame geometry sampling (currently it only reads keyframe[0]).

5. **Implement per-frame keyframe evaluation in `PlayerAppController`.**
   Replace the first-keyframe-only geometry snapshot with proper `Evaluate(effectiveChartTimeMs)` calls for arena/lane tracks. This is required before the debug overlay geometry can track animated lanes correctly and before any real chart feels right.

6. **Add scoring accumulation and results display.**
   Implement a simple `ScoreAccumulator` (counts Perfect+/Perfect/Great/Miss, hold tick hits/misses; computes a running total). Display counts in the `OnGUI` overlay. This unlocks the ability to verify scoring feel with `PerfectWindowCoversGreatWindow`.

7. **Build and test `PlayerDebugRenderer` flick arrow on device (or in Editor with touch emulation).**
   Verify the arrow color/length/Z-bias is readable on the cone surface and the arrow direction is visually correct for `lane-1` at 250° (expect U arrow to point lower-left in screen space, toward the arena center).

8. **Implement miss detection.**
   Notes past `timeMs + GreatWindowMs` that are still `Active` should be marked `Miss` and emitted as `JudgementRecord`s. Currently misses are silent (notes just stay `Active` indefinitely). `NoteScheduler` or `PlayerAppController` needs a sweep for expired notes each frame.

9. **Create a second test difficulty (`hard.json`) with overlapping lanes.**
   Exercise the multi-note arbitration path (spec §7.6): place two overlapping lane regions and simultaneous notes in both. Confirm the tie-break (timing error → angular distance → priority → noteIndex) behaves as specified via `DebugLogFlick` and the `OnGUI` last-judgement display.

10. **Begin Chart Editor App wiring.**
    `EditorProject`, `UndoStack`, and `RpkExporter` are stubbed but not connected to any UI. The next editor step is to wire a minimal timeline view that can load a `.rproj.json` file, display notes as blocks, and export a valid `.rpk`. Start with the data-layer round-trip: load → display note count → export → validate exported pack with `ChartValidator`.
