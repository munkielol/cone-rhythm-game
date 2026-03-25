# **Rhythm Game Chart Editor v0 Specification**

**Document version:** 0.5 (v0 Claude-ready)
**Primary goal:** Provide a Windows Unity editor to author charts and export playable `.rpk` song packs for the mobile player app.  
**Core principle:** Everything is authored in **timeMs** (integer milliseconds). Beat/grid tooling is **derived** from the tempo map and used only for editor UX.

---

## **1\) Platform and tech**

* **Chart Editor platform:** Windows PC only (v0)  
* **Engine:** Unity (same engine as player app)  
* **Primary input:** mouse \+ keyboard  
* **Playtest input:** **single-touch emulation only** (mouse \= 1 touch). Multi-touch emulation is out of scope for v0.

---

## **2\) Time system and tempo map**

### **2.1 Authoring timeline coordinate**

* All chart timestamps are stored as **`timeMs` int** from chart start:  
  * notes (tap / flick / catch)  
  * holds (start/end \+ baked tick times)  
  * arena keyframes  
  * lane keyframes  
  * camera keyframes

### **2.2 Tempo segments (time-keyed)**

Tempo is stored as non-overlapping segments sorted by `startTimeMs`.

**Segment types**

* **constant**  
  * `startTimeMs: int`  
  * `bpm: float > 0`  
* **ramp** (linear in time)  
  * `startTimeMs: int`  
  * `endTimeMs: int > startTimeMs`  
  * `startBpm: float > 0`  
  * `endBpm: float > 0`

**Rules**

* First segment must start at `startTimeMs = 0`  
* No overlaps, strictly increasing segment starts

### **2.3 Derived beat grid (chart editor-only)**

* chart editor provides beat snapping and beat ruler by deriving:  
  * `BeatAtTime(timeMs)` (cumulative beats)  
  * `TimeAtBeat(beat)` (inverse)  
* Supports ramps (BeatAtTime becomes quadratic, TimeAtBeat solved by quadratic within ramp segments).  
* Beat grid divisions offered to users (minimum v0):  
  * `1/4, 1/8, 1/12 (triplets), 1/16, 1/24, 1/32`

---

## **3\) chart editor views and layout (v0)**

### **3.1 Waveform Timeline (navigation)**

* Horizontal waveform \+ time ruler (ms / seconds)  
* Primary use:  
  * scrub to transient  
  * zoom (down to ms-level)  
  * loop region  
  * place tempo segments

### **3.2 Note Canvas (primary chart editing)**

* **Time flows top → bottom** (notes “fall” toward a hit line near the bottom).  
* **X-axis is lane columns** (readability-first):  
  * one column per `laneId`  
  * columns grouped by arena:  
    * **Arena A columns** group  
    * **Arena B columns** group  
* This view is intentionally not “spatially true”; it’s for readable editing.  
* The chart editor stores lane column order (per arena group) in chart editor project metadata.  
* Exported runtime charts do not need this ordering.

### **3.3 Playfield Preview (spatial truth at playhead)**

* Shows the true arena \+ lane geometry **at the current playhead time**.
* Used to:
  * confirm overlap/visibility/motion
  * click a lane to set “active lane” for placement
* (v0) May be rendered as 2D top-down; a 3D preview is optional and can be added later.

**Shared evaluation utilities (required for Playfield Preview):**

The chart editor Playfield Preview **must** reuse the same Shared evaluation infrastructure as the player app. Do not reimplement keyframe evaluation, approach math, or geometry formulas in editor-only code — doing so creates divergence that can only be caught at runtime.

* `ChartRuntimeEvaluator` (`Assets/_Project/Shared/Runtime/Evaluation/ChartRuntimeEvaluator.cs`) —
  call `Evaluate(playheadMs)` each update to sample all arena/lane/camera geometry with zero allocations.
  Returns `EvaluatedArena[]` and `EvaluatedLane[]` (pre-allocated arrays; safe to call every frame).
* `NoteApproachMath` (`Assets/_Project/Shared/Runtime/Evaluation/NoteApproachMath.cs`) — **parity-critical.**
  Static helpers for `JudgementRadius`, `ApproachRadius`, `FrustumZAtRadius`, `LaneChordWidthAtRadius`, etc.
  These must be used for all note approach rendering in the preview so the visual matches the player exactly.
  Any formula in the preview that differs from these helpers is a bug.
* `EvaluatedArena` / `EvaluatedLane` / `EvaluatedCamera` structs
  (`Assets/_Project/Shared/Runtime/Evaluation/EvaluatedGeometry.cs`) —
  fully evaluated per-frame state; no keyframe math required after `Evaluate()`.

**Arena fill intervals in the Playfield Preview:**
The Playfield Preview must show fill intervals (the non-lane portions of each arena span) as runtime-derived visualization — not as authored objects. Fill intervals update dynamically as the playhead changes and lane geometry evaluates to new positions and widths. The preview must not treat fill intervals as selectable or exportable content; they are derived from the complement of current lane occupancy (player spec §5.5.3) and must use the same evaluated lane geometry as all other preview rendering.

**Arena surface mesh in the preview must animate:**
The arena surface mesh (cone/frustum sector) must be rebuilt whenever evaluated arena parameters
change — the same change-detection pattern used in `PlayerDebugArenaSurface` (compare outerRadius,
bandThickness, arcStartDeg, arcSweepDeg, center against watermarks; rebuild vertices in-place on
change).  A preview that builds the mesh once at `t=0` and never updates it will show lane overlays
animating while the surface stays frozen — the same bug that existed in the player debug scaffolding
before it was fixed.

**Note body rendering in the Playfield Preview:**

The Playfield Preview must render note bodies using the same geometry rules as the player app. The goal is pixel-accurate preview fidelity so charters see exactly what players will see.

* **Note head width** — use `NoteApproachMath.LaneChordWidthAtRadius(r, halfWidthDeg) × noteLaneWidthRatio`. Must be computed per-frame from the current evaluated lane geometry, not baked at a fixed width.
* **Note head Z (frustum placement)** — use `NoteApproachMath.FrustumZAtRadius` with the same frustum parameters as the player arena surface. Notes must sit on the cone surface, not hover flat at Z=0.
* **Approach radius** — use `NoteApproachMath.ApproachRadius` with the same `noteLeadTimeMs` (2000 ms) and `spawnRadiusFactor` (0) as the player renderers.
* **Flick notes** — show a direction arrow overlay consistent with the flick arrow billboard spec (player spec §5.7.2). The arrow must point in the gesture direction (§player spec §7.3.1) and remain constant size (not scale with lane width).
* **Hold bodies** — use the arc-conforming grid geometry and head cap rules from player spec §5.7.1 (approach formula, arc-grid body, hold head cap, three-phase visibility, color/state mapping). Do not use the superseded `tangLocal` trapezoid approach.

**Implementation approach:** The chart editor playfield preview can directly reference and instantiate `TapNoteRenderer`, `CatchNoteRenderer`, `FlickNoteRenderer`, `HoldBodyRenderer`, and `NoteSkinSet` from the Player assembly (or Shared equivalents when factored out). This eliminates code duplication and ensures automatic parity. This reuse remains valid regardless of whether the runtime skin implementation is currently CPU-driven UV assignment or later becomes shader-optimized — the `NoteSkinSet` authoring data contract does not change between those two implementations.

**Shared production source-of-truth contract (must not diverge between gameplay and editor preview):**

The following systems and data contracts must be shared — not reimplemented — between gameplay and Playfield Preview:

| System | Owner | Parity requirement |
|---|---|---|
| `ChartRuntimeEvaluator` | `Assets/_Project/Shared/` | Same evaluation path, same evaluated struct types |
| `NoteApproachMath` | `Assets/_Project/Shared/` | Same formula for every helper; any change takes effect in both apps |
| `EvaluatedArena` / `EvaluatedLane` / `EvaluatedCamera` | `Assets/_Project/Shared/` | Same struct layout; no parallel structs in editor code |
| `NoteSkinSet` | `Assets/_Project/Player/Runtime/Skins/` | Same texture/material contracts; preview renders from the same asset |
| `PlayfieldFrustumProfile` | `Assets/_Project/Player/Runtime/Visuals/` | Same frustum heights used for note Z placement in both preview and gameplay |
| Note renderer geometry (fixed-edge + tiled-center, curved-cap) | `NoteCapGeometryBuilder` | Preview must use the same geometry rules; full-stretch UV is transitional only |

**Production feedback preview direction:**
The Playfield Preview should eventually preview the same `GameplayFeedbackSkinSet` (player spec §5.12) assets used in gameplay:
* Lane touch indicator preview — show which lane is "active" when a charter clicks/drags in the preview.
* Judgement feedback preview — show a sample Perfect/Great/Miss effect at the judgement ring so charters can preview timing feel and visual clarity.

These feedback previews are not required for v0 charting functionality. An abstract placeholder (no feedback shown) is acceptable as a transitional state. When implemented, they must read from the same `GameplayFeedbackSkinSet` contract as gameplay — no editor-only reimplementation.

**Note skin preview:**
The Playfield Preview must reflect the full production `NoteSkinSet` appearance. An abstract color-only or full-stretch-UV fallback is acceptable only as a transitional interim step — it is not the intended final preview path:
* Lane-width-aware note bodies using `noteLaneWidthRatio` from the active `NoteSkinSet`.
* **Fixed-edge + tiled-center texture layout** (player spec §5.7.3) — decorative borders must not stretch when lane width changes; the center must tile, not stretch. The preview must use the same CPU-driven per-frame UV assignment as the player renderers so behavior is pixel-accurate.
* The same `noteBodyMaterial` shader template and per-type body textures (`tapBodyTexture`, `catchBodyTexture`, `flickBodyTexture`) from `NoteSkinSet` — not a simplified single-color substitute.
* Flick arrow materials from the skin set (Up/Down/Left/Right as configured).
* Hold ribbon color states (approaching / active / released) from `HoldBodyRenderer` color params.

An abstract debug geometry view (outline-only quads, solid colors) is acceptable as a **transitional** interim step, but the skin preview contract above is the target and must not be permanently simplified away.

### **3.4 Inspector / Properties panel**

* Selecting any object (arena/lane/camera/note/hold tick) exposes editable fields.  
* Supports multi-select editing where safe (e.g., nudging time).

---

## **4\) Editing fundamentals (v0)**

### **4.1 Selection and manipulation**

* Single select, multi-select, box select (Note Canvas)  
* Drag notes in time (snaps apply)  
* Reassign notes to lane by drag between columns (Note Canvas) and/or via inspector  
* Copy/paste and duplicate (preserve relative timing)  
* Nudge time by snap step (keyboard)

### **4.2 Undo/redo**

* Full undo/redo for all chart edits.

---

## **5\) Authoring objects and animation tracks**

### **5.1 Keyframe tracks**

All animated params are stored as keyframe lists:

{  
  "keyframes": \[  
    { "timeMs": 0, "value": 0.5, "easing": "linear" },  
    { "timeMs": 1000, "value": 0.6, "easing": "easeInOut" }  
  \]  
}

**Easing enum (v0):**

* `"linear"`  
* `"easeInOut"`  
* `"hold"` (step)

### **5.2 Enabled vs opacity**

* `enabled`: numeric **0/1** track (interaction toggle; also useful for preview filtering)  
* `opacity`: float track **visual-only** (0..1); can be 0 while enabled=1 (allowed; chart editor warns)

**Export/validation constraints (locked):**

* `enabled` keyframe values must be exactly **0** or **1**.
* `enabled` keyframes must use easing **`"hold"` only**.
* Violations are **export-blocking errors** (see §12.1).


---

## **6\) Arena authoring (v0)**

**Angle convention (locked):** `0°` is **+X (to the right)**, angles increase **CCW**, normalized to `[0,360)`.


An arena is an annular arc band (can become full ring).

**Arena fields**

* `arenaId: string`  
* Tracks (all keyframed by timeMs):  
  * `enabled` (0/1)  
  * `opacity` (0..1)  
  * `centerX` (0..1)  
  * `centerY` (0..1)  
  * `outerRadius` (normalized)  
  * `bandThickness` (normalized)  
  * `arcStartDeg`  
  * `arcSweepDeg` (0..360; 360 \= full ring)

**Notes**

* Normalized coordinates are in chart playfield space (0..1).
* **Aspect-safe rule (locked):** any circle/radius math in previews or playtest must convert normalized radii to a consistent local unit using the **minimum dimension** of the preview playfield rectangle (same as player).
  * `minDimLocal = min(playfieldWidthLocal, playfieldHeightLocal)`
  * `outerLocal = outerRadiusNorm * minDimLocal`
  * `bandLocal = bandThicknessNorm * minDimLocal`
* The runtime maps normalized values through a PlayfieldTransform to world space; editor preview/playtest must match the same math.

**chart editor tools**

* Add/move/delete keyframes at playhead  
* Drag keyframes in time  
* Set easing per keyframe  
* Quick “make full ring” (set sweep=360) convenience button

---

## **7\) Lane authoring (v0)**

A lane is an angular slice inside an arena band.

**Lane fields**

* `laneId: string`  
* `arenaId: string`  
* `priority: int` (primarily render order / rare tie-break)  
* Tracks:  
  * `enabled` (0/1)  
  * `opacity` (0..1)  
  * `centerDeg`  
  * `widthDeg`

**Fill regions are derived, not authored (locked):**
Lanes are the only chart-authored angular objects within an arena. The fill intervals between and around lanes — which visually occupy the currently unoccupied portions of the arena span — are runtime-derived from evaluated lane geometry (player spec §5.5.3). They are not authored as lane objects and must not appear in `lanes[]`. Do not create fake filler lanes to fill inter-lane visual gaps; the runtime computes fill intervals automatically from the complement of current lane occupancy.

**Overlap**

* Lanes are allowed to overlap partially or fully.
* chart editor should provide warnings for potentially confusing overlaps (especially near hit time).

**chart editor tools**

* Create/duplicate lane  
* Assign lane → arena  
* Angle editing helpers (wrap-aware)  
* Ordering UI (by arena group; within group user-sortable)

### **7.1) Lane editing interaction rules (v0)**

You asked if rules exist—yes, you already have the core tracks and tools, but add these explicit UX rules so implementation is clear.

* Lanes are edited via keyframed tracks:  
  * `centerDeg` controls lane movement around the arena.  
  * `widthDeg` controls lane size.  
  * `opacity` controls visibility (visual-only).  
  * `enabled` controls interaction.  
* chart editor must support:  
  * Create lane, duplicate lane  
  * Assign lane → arena  
  * Add/move/delete keyframes for `centerDeg` and `widthDeg`  
  * Wrap-aware angle editing (0–360)  


**Create / delete semantics (locked for v0):**

* **Add Lane** creates a new lane with defaults and writes keyframes at the current playhead time:
  * `enabled = 1` (hold)
  * `opacity = 1` (linear/hold allowed, default hold)
  * `centerDeg` placed evenly among existing lanes in that arena at the playhead time
  * `widthDeg` set to the current project default (chart editor setting)
  * `priority` default = next highest priority in the arena group
* **Delete Lane** is blocked if any note references that `laneId`.
  * User must reassign or delete those notes first (chart editor should provide a clear error message listing counts).

* Playfield Preview “direct manipulation” (recommended v0, but can be simple):  
  * At the current playhead time, allow dragging a lane handle to set `centerDeg` (writes/updates a keyframe at playhead).  
  * Allow dragging width handles to set `widthDeg` (writes/updates a keyframe at playhead).  
  * This is optional, but if you include it, it will massively speed charting.

---

## **8\) Camera authoring (v0)**

Camera tracks are authored in timeMs and exported as chart camera tracks.

**Camera fields (suggested v0)**

* `enabled` (0/1)  
* Position: `posX,posY,posZ`  
* Rotation: `rotPitchDeg,rotYawDeg,rotRollDeg`  
* `fovDeg`

**Important runtime rule (documented in chart editor UI):**

* Camera motion is supported in gameplay; input mapping remains correct via ray → playfield plane (ArcCreate-style). The chart editor playtest should approximate this if possible.

---

## **9\) Note types and authoring (v0)**

### **9.1 Common note fields**

* `noteId: string GUID` (required; stable for selection/undo/export; runtime may ignore except logging)  
* `laneId: string`  
* `type: "tap" | "flick" | "catch" | "hold"`  
* `judging: bool` is **optional**  
  * Defaults to **true** if omitted  
  * Only include `"judging": false` for no-input/visual-only notes (to keep files readable)

### **9.2 Tap**

* Fields: `timeMs`  
* Placement: click lane column at playhead, or “place at playhead” on active lane

### **9.3 Flick**

* Fields: `timeMs`, `direction`
* `direction` enum: `"L" | "R" | "U" | "D"`
* Meaning is **lane-relative** at the note time (player-facing-inward frame; see player spec §7.3.1):

| direction | meaning |
|---|---|
| `U` | inward toward arena center |
| `D` | outward from arena center |
| `L` | clockwise tangential (left when facing inward) |
| `R` | counter-clockwise tangential (right when facing inward) |

* Chart editor preview arrow **must use the same mapping** (derive expected vector from lane center angle θ exactly as in player spec §7.3.1).

**Judgement intent (for playtest/readability):**

* Flick is **event-based**: each distinct qualifying gesture during a continuous touch can match a separate note (supports rapid U→D patterns without lifting). See player spec §7.3.
* Default tiers: **Perfect / Great / Miss** based on timing. Toggle `FlickPerfectWindowCoversGreatWindow` to suppress the Great tier so all in-window flicks score Perfect.

### **9.4 Catch (single note)**

* Fields: `timeMs`  
* Each catch is a **single note** (not a duration object).  
* Used to author “guided movement” by placing sequences of catch notes across lanes/times.

**Judgement intent (for playtest/readability):**

* Catch is **Perfect-or-Miss** based on presence in lane at the time.

### **9.5 Hold (chart editor-baked ticks, deterministic)**

Holds store explicit tick times in the exported chart.

Hold fields:

* `startTimeMs`
* `endTimeMs`
* `tickTimesMs: int[]` (strictly increasing; all within `[start,end]`)

**Runtime scoring note:** `tickTimesMs` are the scoring units in the player app. Each tick
contributes to combo and score (Perfect-or-Miss, 1000/0 pts — player spec §4.4). The player
consumes the baked list directly; no runtime tick cadence generation occurs.

chart editor-only metadata allowed:

* `chart_editor.tickPreset: "1/8"` etc. (runtime ignores)

**Hold body visual (player + playtest):**
The player renders holds as a **head cap** (at `headR`) plus an **arc-conforming body ribbon** that approaches the judgement line (see player spec §5.7.1). The head is part of the intended design — not a placeholder.

The ribbon spans from the tail (`endTimeMs` approach position) to the head (`startTimeMs` approach position).
Once `chartTime ≥ startTimeMs`, the head is pinned at the judgement ring and the ribbon shrinks as the tail catches up. The head **does not overshoot** — it is pinned naturally by `Clamp01` in the approach formula.

In v0 the ribbon uses the **current lane geometry** (center angle, width) each frame — no bending.

If the hold is **missed** (player never bound it) or **released early**, the ribbon continues to shrink geometrically until `endTimeMs` using a dim non-judging color (`holdColorReleased`). Judging stops immediately; the visual persists so the player can see the missed hold's extent. After `endTimeMs` the ribbon disappears.

> ⚠️ **Conflict resolved:** A prior draft described the ribbon body as "a trapezoid (v0 single-segment)" with `tangLocal` offset vertices. That was the Step 1 geometry and is superseded. The current body is an **arc-conforming grid** as described below.

The body is an **arc-conforming grid** (see player spec §5.7.1 "Body geometry"): vertices are placed at `(ctr + cos(θ)·r, ctr + sin(θ)·r, FrustumZAtRadius(r) + NoteLayerZLift)` across a `(holdArcSegments+1) × (holdRadialSegments+1)` grid. This makes the body follow the lane's actual arc curvature. The chord width formula (`2 · r · sin(holdHalfAngleDeg · Deg2Rad)`) is used for UV mapping only; the `holdLaneWidthRatio` skin parameter scales the angular half-span (default 0.9). The chart editor playtest preview must use the same arc-conforming formula — not the old `tangLocal` trapezoid.

**Body motion readability:** The body UV is head-anchored — the pattern at the judgement end stays stable while tiles disappear from the tail as it is consumed. There is no independently scrolling UV pattern. The motion impression comes from body geometry and consumption behavior. This must hold with or without the hold head cap displayed.

**Hold endpoint model (v0):** The v0 hold is start-judged only (start event + baked ticks). There is no required release-endpoint judgement in v0. A future hold-end note type is deferred — do not author charts expecting endpoint judgement, and do not add chart schema fields for it now.

Spawn position: `spawnRadiusFactor = 0` (v0 default) → hold tails first appear at the inner band edge (`innerLocal`) and travel outward.

**Hold skin philosophy (later step — not part of initial Tap/Catch/Flick skin implementation):**
Hold body skins must follow the same fixed-edge + tiled-center philosophy as note head skins (player spec §5.7.3). Hold skin migration is explicitly a later step; the initial skin work covers Tap/Catch/Flick only:
* Decorative side borders must preserve their art at fixed UV-mapped widths regardless of lane width changes. Stretch is not acceptable as a final result.
* The center region must tile between the borders — not simply stretch. Full-surface stretch is a transitional placeholder only.
* The ribbon must tile along the radial (length) direction rather than stretching, keeping art stable across different hold durations. The tiling is body-local and head-anchored — not a separate UV scroll animation (see player spec §5.7.1 "Body motion readability").
* The current `HoldBodyRenderer` drives texture via `MaterialPropertyBlock._MainTex` / `_Color` with head-anchored V tiling. Full fixed-edge + tiled-center skin migration is a deferred later step.
The chart editor playtest should eventually render hold bodies via `HoldBodyRenderer` (or a shared equivalent) with full `NoteSkinSet`-driven skin fidelity, once hold skin migration is implemented.

---

## **10\) Hold tick generation and editing (v0)**

**Tick density guidance:** Because each tick is a scored event (+1000 pts, combo++), tick
density directly determines the scoring weight of each hold. Denser grids reward sustained
holding more heavily. The default 1/8 preset provides a balanced feel for typical BPM ranges.

### **10.1 Default tick preset**

* Default generation preset: **1/8 beat per tick**

### **10.2 Allowed preset divisions (v0)**

* `1/4, 1/8, 1/12, 1/16, 1/24, 1/32`

### **10.3 Generation behavior**

* Charter defines hold start/end times (timeMs).  
* chart editor uses tempo map to:  
  1. derive startBeat/endBeat  
  2. step beats by preset division  
  3. convert each tick beat back to **tickTimeMs** (int)  
* Store baked times in `tickTimesMs`.

### **10.4 Manual tick editing (allowed)**

* After generation, charter can:  
  * add/delete ticks  
  * nudge selected ticks by ms  
  * regenerate ticks from a different preset

### **10.5) Hold tick bake rounding \+ de-dup policy**

You already say tickTimes are `int` ms, but you haven’t specified rounding behavior.  
**Spec:**

* When converting generated tick beats → `timeMs`:  
  * Use `roundToInt` (nearest millisecond).  
* After generation:  
  * Sort ascending.  
  * Remove duplicates created by rounding.  
* If de-dup would remove “too many” ticks (e.g., \>10% of ticks), show a warning suggesting a coarser division or longer hold.

---

## **11\) chart editor playtest (v0)**

* Runs gameplay simulation using the same:
  * timeMs chart evaluation
  * lane hit-testing (**aspect-safe local math derived from normalized params**, matching the player)
  * judgement windows/scoring profile behavior as closely as possible
* **Input:** single touch only (mouse)
* Debug overlays (recommended):
  * show note IDs
  * show timing error in ms
  * show lane boundaries at playhead time
  * show whether a lane is enabled vs only visible

### **11.1 v0 playtest toggles**

The chart editor playtest should expose the same v0 debug toggles as the player (see player spec §8.3.1). These do not affect the exported chart.

The **visual surface raycast** toggle (`useVisualSurfaceRaycast`) from player spec §5.2.1 also applies to playtest: if the editor scene has a `MeshCollider` on the arena surface GO (same layer setup as the player), the playtest controller should use the same parallax-correct projection. This is optional for v0 — the flat-plane fallback is acceptable for authoring.

**Playtest production parity:** The chart editor playtest must not depend on `PlayerDebugArenaSurface` or `PlayerDebugRenderer` for correct rendering or input. The same production components (`ArenaColliderProvider`, `PlayfieldFrustumProfile`, `ArenaBandRenderer`, `LaneGuideRenderer`, `JudgementRingRenderer`, note renderers) must be usable in the editor playtest context as they are in gameplay. Debug overlays are additive and optional.

Flick playtest uses the same **event-based** model as the player: each qualifying gesture emits a `FlickEvent` that is matched to a note. Rapid flick sequences can be authored and tested within a single continuous mouse-down.

| Toggle | Default | Playtest effect |
|---|---|---|
| `PerfectWindowCoversGreatWindow` | `false` | Suppresses Great tier for tap/hold during playtest to verify lenient-feel charting. Does not affect flick. |
| `FlickRequireTouchBegin` | `false` | When true, only gestures completed within `FlickMaxGestureTimeMs` of mouse-down are eligible. When false (default), allows testing flick notes with mouse-down held throughout (single-touch emulation). |
| `FlickPerfectWindowCoversGreatWindow` | `false` | When true, suppresses the Great tier for flick during playtest — all in-window flicks score Perfect. Useful for verifying lenient flick-feel charting. |
| `HitBandInnerCoverage01` | `0.35` | Fraction `[0..1]` of available inward depth (from judgement line toward `chartInnerLocal`) accepted as a hit. Matches the player setting (see player spec §5.5.2 / §8.3.1). Does not affect exported chart geometry. |
| `HitBandOuterInsetNorm` | `0.04` | Outward half-width of the hit band from `judgementRadiusLocal`. Matches the player setting. Does not affect exported chart geometry. |
| `InputBandExpandInnerNorm` | `0.00` | Additional inner expansion subtracted from `hitBandInner` (fine-tune). Matches the player setting. Does not affect exported chart geometry. |
| `InputBandExpandOuterNorm` | `0.03` | Additional outer expansion added on top of `hitBandOuter`, clamped to `visualOuterLocal`. Matches the player setting. Does not affect exported chart geometry. |
| `JudgementInsetNorm` | `0.003` | **Visual/skin.** Insets the judgement ring inside chart `outerLocal` during playtest. Notes approach and land on this inset ring. Matches the player setting (see player spec §5.8 / §8.3.1). Does not affect exported chart geometry or hit-testing. |
| `VisualOuterExpandNorm` | `0.00` | **Visual/skin.** Extends the arena mesh/arc rim beyond chart `outerLocal` during playtest. Matches the player setting. Default 0 = rim at `outerLocal`. Does not affect exported chart geometry or hit-testing. |

---

## **12\) Validation (export gate \+ warnings)**

### **12.1 Errors (block export)**


* Keyframe track validation (all arenas/lanes/camera tracks):
  * **required tracks must have at least 1 keyframe** (0 keyframes is export-blocking)
  * keyframes must be sorted by `timeMs`
  * **duplicate `timeMs` is an export-blocking error**
* Enabled track validation:
  * values must be exactly 0 or 1
  * easing must be `"hold"` only
* Tempo segments invalid:  
  * missing segment at 0  
  * overlaps / unsorted  
  * invalid BPM (≤0) or invalid ramp end ≤ start  
* Any `laneId` referenced by a note missing  
* Any `arenaId` referenced by a lane missing  
* Hold tick validation:  
  * tickTimes not strictly increasing  
  * ticks outside `[startTimeMs,endTimeMs]`

### **12.2 Warnings (do not block export)**

* enabled=1 and opacity≈0 (interactive but invisible)  
* excessive simultaneously enabled lanes (readability/perf risk)  
* heavy overlap at/near hit time (ambiguity risk)  
* charts likely requiring multi-finger chords (since chart editor playtest is single-touch)

---

### **12A) chart editor project file (locked for v0)**

* chart editor uses a `*.rproj.json` project file as the **authoritative source of truth** for all authoring data (v0 locked):  
  * paths to imported OGG \+ jacket sources  
  * song metadata inputs (title/artist/preview range)  
  * all difficulties’ chart data  
  * chart editor-only metadata (lane column ordering, last selected objects, etc.)  
* Export uses the project to generate `.rpk`.

## **13\) Export pipeline (v0)**

### **13.1 Export outputs**

* A **song pack** file: `.rpk` (zip container, compression only)

### **13.2 `.rpk` contents (required)**

songinfo.json  
audio/song.ogg  
jacket/jacket\_\<size\>.png  
charts/\<difficultyId\>.json

* Audio format: **OGG**  
* Jacket images: **any subset** of  
  * `jacket_256.png`, `jacket_512.png`, `jacket_1024.png`  
* Charts: **one JSON per difficulty** (locked)

### **13.3 `songinfo.json` generation (must-have)**

chart editor generates `songinfo.json` automatically at export.

Required fields:

* `packageVersion`  
* `songId`  
* `title`  
* `artist`  
* `lengthMs`  
* `bpmDisplay { min, max }` (auto-derivable from tempo; user-editable)  
* `audio { path }`  
* `jacket { images: [{ size, path }] }`  
* `charts: [{ difficultyId, path }]`  
* `preview { startTimeMs, endTimeMs }` (optional but supported)

Optional (recommended):

* `hashes: { "<path>": "sha256:<hex>" }` for integrity

### **13.4 Jacket resizing (chart editor convenience)**

* If user provides one high-res jacket, chart editor can auto-generate 256/512/1024 on export (toggle).

### **13.5 Manual workflow (v0)**

* No “send to device” button required.  
* Manual export of `.rpk` is sufficient.

### **13.6) OGG-only audio import (v0)**

* The chart editor accepts **only** OGG audio for v0.  
* Import validation:  
  * If audio is not `.ogg`, reject with a clear error message.  
* Export always writes `audio/song.ogg` exactly as imported (no transcoding pipeline in v0).

---

## **14\) Chart JSON schema (exported per difficulty, v0)**

Top-level:

* `formatVersion: 1`  
* `song { songId, difficultyId, audioFile, audioOffsetMs }`  
* `tempo { segments[] }`  
* `arenas[]`  
* `lanes[]`  
* `camera`  
* `notes[]`

Key decisions:

* `enabled` tracks are numeric **0/1**  
* `judging` defaults true if omitted (only include when false)

(Your current detailed schema examples are already locked; the chart editor must export exactly to that contract.)

**`song.audioOffsetMs` meaning (locked, must match player):**

* `audioOffsetMs` is an int (default 0).
* Positive means **judge later** (chart evaluation occurs later relative to audio).
* Player uses: `effectiveChartTimeMs = songDspTimeMs + audioOffsetMs + UserOffsetMs`.

**Canonical field lists (v0 export contract, no guessing):**

* `arenas[]` each contains:
  * `arenaId: string`
  * tracks: `enabled, opacity, centerX, centerY, outerRadius, bandThickness, arcStartDeg, arcSweepDeg`
* `lanes[]` each contains only **interactive authored lanes** — fill intervals between lanes are runtime-derived and never serialized (player spec §5.5.3). Including fake filler lanes to pad visual gaps is an authoring error.
  * `laneId: string`, `arenaId: string`, `priority: int`
  * tracks: `enabled, opacity, centerDeg, widthDeg`
* `camera` contains tracks (suggested v0):
  * `enabled, posX,posY,posZ, rotPitchDeg,rotYawDeg,rotRollDeg, fovDeg`
* `notes[]` entries:
  * Common: `noteId, laneId, type, judging?`
  * Tap: `timeMs`
  * Flick: `timeMs, direction`
  * Catch: `timeMs`
  * Hold: `startTimeMs, endTimeMs, tickTimesMs[]`


---

## **15\) Explicit out-of-scope (v0)**

* Multi-touch emulation on PC  
* Plugin/macro scripting system (can be added later)  
* Accounts/cloud/chart editor collaboration features  
* One-click deployment to phone  
* Story/world/events/economy tooling

---

## **16\) Open items (not decided yet, safe to defer)**

These are not blocking implementation, but should be tracked:

* Whether Playfield Preview is 2D-only in v0 or also offers a 3D preview matching runtime.  
---

