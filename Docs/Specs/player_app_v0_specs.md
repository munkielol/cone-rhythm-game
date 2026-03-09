# **Rhythm Game Player App v0 Specification** 

**Document version:** 0.3 (v0 Claude-ready)  
**Primary goal:** Play `.rpk` song packs end-to-end on mobile to validate feel, timing, input, and visuals.  
**v0 scope:** Core gameplay only. No meta systems (accounts/story/events/currency).

---

## **1\) Platform and tech**

* **Platforms:** iOS \+ Android  
* **Engine:** Unity  
* **Audio clock:** DSP-time driven conductor  
* **Frame target:** stable 60fps on mid-range devices

---

## **2\) Content format and loading**

### **2.1 Song pack container**

* File extension: `.rpk` (ZIP container)  
* **Compression only** (no encryption/signing in v0)

### **2.2 Required pack structure**

songinfo.json  
audio/song.ogg  
jacket/jacket\_\<size\>.png  
charts/\<difficultyId\>.json

### **2.3 `songinfo.json` fields (consumed by player)**

* `packageVersion`  
* `songId`  
* `title`  
* `artist`  
* `lengthMs`  
* `bpmDisplay { min, max }` (display only)  
* `audio.path`  
* `jacket.images[]` list with `{ size, path }` (any subset of 256/512/1024)  
* `charts[]` with `{ difficultyId, path }`  
* optional `preview { startTimeMs, endTimeMs }`  
* optional `hashes { path: sha256 }` (integrity check)

### **2.4 Chart file rule**

* One JSON per difficulty (`charts/<difficultyId>.json`)

### **2.5 Loading/validation (v0)**

On selecting a song+difficulty:

* Read `songinfo.json`  
* Load jacket image best-fit available  
* Load chart JSON and validate:  
  * `formatVersion` supported  
  * tempo segments valid  
  * notes reference valid `laneId`  
  * lanes reference valid `arenaId`  
  * hold tick times monotonic and in bounds  
* Load audio `audio/song.ogg`

### **2.6) Pack discovery and “manual import” rule (v0)**

**Goal:** Make “Enumerate installed .rpk packs” unambiguous.

* The player scans for packs in: `Application.persistentDataPath/Packs/`  
* Any file matching `*.rpk` in that directory is treated as a candidate song pack.  
* On startup and on returning to Song Select, rescan this directory.  
* If a pack fails to load (missing files / invalid JSON / invalid chart), the app:  
  * **excludes it from the list and logs the reason** (v0 locked behavior).
  * Log includes: pack filename, failing path, and validation error summary.

### **2.7) Audio Format Restriction: OGG-only audio (v0)**

* The player **only** loads `audio/song.ogg` from the `.rpk`.  
* No runtime support for other audio formats in v0.

---

## **3\) Timing and tempo system**

### **3.1 Time base**

* **All authored times are `timeMs` int** from chart start:  
  * notes  
  * holds \+ tickTimes  
  * arena/lane/camera keyframes  
  * tempo segment boundaries

### **3.2 Tempo map (time-keyed, ramps supported)**

Tempo segments:

* constant: `{ startTimeMs, bpm }`  
* ramp (linear in time): `{ startTimeMs, endTimeMs, startBpm, endBpm }`

Tempo map is used for:

* editor beat grid  
* (player) optional display/analytics  
* holds are **not** runtime-generated: hold `tickTimesMs` are authoritative.

### **3.3 Timing offsets (chart + user) (v0)**

**Goal:** Make timing behavior deterministic and consistent across judgement, visuals, and input.

* Chart may include `song.audioOffsetMs` (int, default 0) to compensate for audio encode/lead-in quirks.
* Player setting `UserOffsetMs` (int) applies on top of the chart offset.
* **Sign convention (locked):** positive offset means **judge later** (notes occur later relative to audio).
* **Effective time formula (locked):**
  * `effectiveChartTimeMs = songDspTimeMs + song.audioOffsetMs + UserOffsetMs`
* Offsets apply to **all time-evaluated chart content** (locked):
  * judgement timing
  * note spawn/approach timing
  * arena/lane/camera keyframe sampling
  * hold tick processing
* Offsets **do not shift audio playback** (audio remains DSP-locked); they shift chart evaluation relative to audio.


---

## **4\) Judgement and scoring**

### **4.1 Mode-specific judgement windows (explicit ms)**

Two modes in v0:

* **Standard**  
  * PerfectWindowMs \= 30  
  * GreatWindowMs \= 90  
  * PerfectPlusWindowMs \= 15 (display-only)  
* **Challenger**  
  * PerfectWindowMs \= 22  
  * GreatWindowMs \= 60  
  * PerfectPlusWindowMs \= 10 (display-only)

### **4.2 Judgement tiers (internal IDs)**

* `Perfect`, `Great`, `Miss`
* Display strings are placeholders in v0 (renameable later).
* **v0 toggle — `PerfectWindowCoversGreatWindow` (default false):** when true, the effective Perfect window is extended to `GreatWindowMs`; Great tier is suppressed and every in-window hit becomes Perfect. Perfect+ sub-window is not affected (see §8.3.1).

### **4.3 Perfect+**

* A **sub-window** inside Perfect for display/stats only (no score change in v0).
* Perfect+ sub-window (`PerfectPlusWindowMs`) is never enlarged by the `PerfectWindowCoversGreatWindow` toggle.

### **4.4 Holds and catches scoring**

* **Hold ticks:** Perfect-or-Miss only (v0)  
* **Catch notes:** Perfect-or-Miss only (v0)  
* TickTimes are editor-baked and deterministic.

### **4.5 Scoring profile**

* Scoring is data-driven via a `ScoringProfile` asset (swappable later).  
* Score goal is accuracy score (not combo-centric).  
* v0 should display:  
  * total score  
  * Perfect+/Perfect/Great/Miss counts  
  * hold tick hit stats

---

## **5\) Playfield \+ 3D rendering model**

### **5.1 True 3D scene**

* Gameplay is rendered in a **true 3D scene**.  
* Lanes/notes are visually represented with depth.

### **5.2 ArcCreate-style input mapping (ray → playfield plane)**

* Touch mapping uses gameplay camera:  
  1. ray \= `GameplayCamera.ScreenPointToRay(touchPos)`  
  2. intersect with **playfield plane**  
  3. convert world hit point → **normalized playfield coords** via `PlayfieldTransform`

### **5.3 Normalized playfield coordinate convention**

* Normalized playfield coords:  
  * `(0,0)` \= **bottom-left** of playable safe area  
  * `(1,1)` \= **top-right**  
* Charts use normalized params (arena centers, radii) in this space.

### **5.4) Playfield plane \+ PlayfieldTransform definition (math-level, v0)**

**Goal:** Make ArcCreate-style ray→plane mapping implementable without guesswork.

* Define a `PlayfieldRoot` transform in world space.  
* The playfield interaction plane is `PlayfieldRoot` local plane where `localZ = 0`.  
* Define a rectangle on that plane representing normalized safe-area coordinates:  
  * `PlayfieldLocalMin` and `PlayfieldLocalMax` (Vector2 in plane local XY).  
  * Mapping:  
    * `NormalizedToLocal(p) = lerp(PlayfieldLocalMin, PlayfieldLocalMax, p)`  
    * `LocalToNormalized(q) = inverseLerp(PlayfieldLocalMin, PlayfieldLocalMax, q)`  
* Touch mapping:  
  * `ray = GameplayCamera.ScreenPointToRay(screenPos)`  
  * intersect with plane `localZ=0` in `PlayfieldRoot` space  
  * convert hit point to `localXY`  
  * `normalized = LocalToNormalized(localXY)`  
* Normalized convention is locked:  
  * `(0,0)` bottom-left of playable safe area; `(1,1)` top-right.

### **5.5 Arena and lane geometry (hit-testing)**

**Angle convention (locked):** `0°` is **+X (to the right)**, angles increase **CCW**, normalized to `[0,360)`.


- Arena is an **annular arc band**:
  - center `(x,y)`, `outerRadius`, `bandThickness`, `arcStartDeg`, `arcSweepDeg`
  - radii are normalized to the **playable safe-area** minimum dimension (implemented via `minDimLocal` in PlayfieldLocal; see algorithm below)
- Lane is an **angular slice** of an arena band:
  - `centerDeg`, `widthDeg`
- Lanes can overlap.
- Lane has no per-lane radial offset in v0.
- **Authoritative interaction space** is the flat playfield plane.
  - Arena visuals may be rendered on a cone-frustum sector using the same arena-local polar parameters `(theta, s)`.
  - Hit-testing/judgement uses the projected playfield-plane coordinates and must not depend on the 3D mesh.
- **Visual rendering may use a cone-frustum sector (visual-only):**
  - Define arena-local polar parameters from **playfield-plane hit point** (PlayfieldLocal) `hitLocalXY`:
    - `centerLocalXY = NormalizedToLocal(arenaCenter)`
    - `v = hitLocalXY - centerLocalXY`
    - `r = length(v)` *(local units)*
    - `theta = atan2(v.y, v.x)` (degrees, normalized to `[0,360)`)
    - `minDimLocal = min((PlayfieldLocalMax.x - PlayfieldLocalMin.x), (PlayfieldLocalMax.y - PlayfieldLocalMin.y))`
    - `outerLocal = outerRadius * minDimLocal`
    - `bandLocal = bandThickness * minDimLocal`
    - `innerLocal = outerLocal - bandLocal`
    - `s = clamp01((r - innerLocal) / bandLocal)` where `s=0` at inner edge and `s=1` at outer edge
  - The arena band exists for interaction if `innerLocal <= r <= outerLocal` **and** `theta` is within the arena arc span (wrap-safe across 0°).
  - **Rendering mapping (frustum surface):** place vertices/notes on a frustum using `(theta, s)`:
    - `R(s) = lerp(innerLocal, outerLocal, s) * visualRadiusScale` *(derived from chart radii)*
    - `Y(s) = lerp(visualHeightInner, visualHeightOuter, s)` *(visual-only height profile)*
    - `posLocal3D = (R(s) * cos(theta), Y(s), R(s) * sin(theta))`
  - **Authoritative rule:** hit-testing + judgement always use `(r, theta)` from **PlayfieldLocal** (projected playfield plane). The frustum is **visual only** and must not affect interaction.
  - **v0 constraint:** `visualRadiusScale` and `visualHeightInner/Outer` are **skin/prefab constants**, not chart-authored parameters.
  - `visualRadiusScale` is a pure visual multiplier applied to **PlayfieldLocal** radii; it must not affect hit-testing.

**Canonical hit-testing algorithm (locked, wrap-safe, aspect-safe):**

**Important:** Do all radius/angle math in **PlayfieldLocal** (the playfield plane’s local XY) to avoid aspect-ratio distortion.

Precompute per-arena (at the evaluated time):

* `centerLocalXY = NormalizedToLocal((centerX, centerY))`
* `playfieldSizeLocal = PlayfieldLocalMax - PlayfieldLocalMin` (Vector2)
* `minDimLocal = min(playfieldSizeLocal.x, playfieldSizeLocal.y)`
* `outerLocal = outerRadius * minDimLocal`
* `bandLocal = bandThickness * minDimLocal`
* `innerLocal = outerLocal - bandLocal`

Given a touch position:

1) Raycast to playfield plane (see §5.2/§5.4) → `hitLocalXY` (PlayfieldRoot local XY)

2) Compute vector from arena center:
* `v = hitLocalXY - centerLocalXY`
* `r = length(v)` (in local units)
* `deg = atan2(v.y, v.x)` converted to degrees, normalized to `[0, 360)`

3) Radial band test:
* Inside band iff `innerLocal <= r <= outerLocal`

4) Arena arc test (wrap-safe):
* If `arcSweepDeg >= 360`, arc test passes.
* Otherwise, let `arcStart = normalize360(arcStartDeg)` and `arcEnd = normalize360(arcStart + arcSweepDeg)`.
* Touch angle is inside the arc iff it lies within the swept interval, using wrap-aware comparison.

5) Lane angular slice test (wrap-safe):
* `laneCenter = normalize360(centerDeg)`
* `halfWidth = widthDeg * 0.5`
* `delta = shortestSignedAngleDeltaDeg(deg, laneCenter)` in `[-180, +180]`
* Inside lane iff `abs(delta) <= halfWidth`

6) Final membership:
* Touch is inside a lane iff band test AND arc test AND lane slice test are all true.

**Input Band Expansion (touch hit-testing only, spec §5.5.1):**

The radial band used for touch arming and judgement may be expanded by configurable normalized margins to account for finger imprecision, especially near the outer rim:

* `expandedInner = innerLocal − (InputBandExpandInnerNorm × minDimLocal)`
* `expandedOuter = outerLocal + (InputBandExpandOuterNorm × minDimLocal)`

The expanded band is used **only** for touch arming and judgement (steps 3–6 above when called from `JudgementEngine`).  It does **not** affect:
* Visual geometry (arena arc outlines, note approach markers).
* Chart-authored geometry (`outerRadius`, `bandThickness`).
* The judgement ring position (see §5.8 — notes land on `judgementRadiusLocal`, not `outerLocal`).
* Hold tick lane membership (`IsInsideFullLane` for hold tick processing).

See §8.3.1 for the default values and the toggle fields.

**Required helper definitions (locked):**
* `normalize360(a)` returns angle in `[0, 360)`.
* `shortestSignedAngleDeltaDeg(a, b)` returns the signed delta from `b` to `a` on the shortest path (range `[-180, +180]`).


### **5.6 Enabled vs opacity**

* `enabled` controls interaction (0/1)  
* `opacity` is visual-only

### **5.7 Note visuals**

* Notes visually occupy the **entire lane width** at that time (expands/contracts with lane width).  
* Applies to tap/flick/catch/hold heads (and holds stretch along approach direction).

### **5.8) Judgement line / hit indicator (v0)**

**Goal:** Provide a strong visual anchor for timing (Arcaea-style inset judgement ring).

The judgement ring sits **inside** the chart outer radius.  The outer rim remains visible beyond it for aesthetics and finger comfort.

**Three radii (all in PlayfieldLocal units):**

| Radius | Formula | Purpose |
|---|---|---|
| `innerLocal` | `outerLocal − bandLocal` | Chart inner edge; hit-testing inner boundary. |
| `judgementRadiusLocal` | `outerLocal − JudgementInsetNorm × minDimLocal` | Where notes land visually; where the judgement arc is drawn. Default: 3 % inside `outerLocal`. |
| `visualOuterLocal` | `outerLocal + VisualOuterExpandNorm × minDimLocal` | Visual mesh / arc outer rim; can extend beyond `outerLocal` for a thick-track look. Default: `outerLocal` (no expansion). |

* **Judgement Arc**: thin, clearly visible arc at `judgementRadiusLocal` per arena (purple-white by default in debug renderer).
* **Note approach**: notes travel from `spawnR` (near inner edge) toward `judgementRadiusLocal`; alpha reaches 1.0 at the judgement ring, not at the chart outer edge.
* **Visual outer rim**: arena surface mesh and outer arc line extend to `visualOuterLocal` so track looks thick beyond the judgement ring. Does not affect hit-testing.
* **Hit-testing is unchanged**: spatial lane membership uses the standard band test (`innerLocal` to `outerLocal`, optionally expanded by `InputBandExpandInnerNorm`/`InputBandExpandOuterNorm`).  `judgementRadiusLocal` is **visual only**.
* The Judgement Arc is always visible during gameplay (unless the arena is disabled).

### **5.9) Keyframe evaluation rules (deterministic, v0)**

* For any keyframed track:
  * If a required track has **0 keyframes**: **invalid chart** (editor blocks export; player rejects pack).  
  * Before first keyframe: hold first value  
  * After last keyframe: hold last value  
  * If two keyframes share the same `timeMs`: **treat as an invalid chart** (export-blocking in editor; player rejects pack as invalid).  
* Easing:  
  * `linear`, `easeInOut`, `hold` as already defined.  

* **Enabled track rules (locked):**
  * `enabled` tracks must use values **0 or 1 only** and must use easing **`hold` only**.
  * If violated, the chart is invalid (editor blocks export; player rejects pack).
  * Runtime evaluates enabled as boolean with: `enabledBool = (value >= 0.5)`.
* Angle tracks (`arcStartDeg`, `centerDeg`, etc.):  
  * Interpolate using **shortest-path wrap** (e.g., 350→10 goes \+20, not \-340).

---

## **6\) Note approach speed (visual-only)**

### **6.1 Fixed distance \+ adjustable speed (Option B)**

Global tuning:

* `SpawnDistanceWorld` (units)  
* `BaseApproachSpeed` (units/sec)  
* `ApproachDirection` (world vector toward hit surface)

Player setting:

* `PlayerSpeedMultiplier`

Effective:

* `ApproachSpeed = BaseApproachSpeed * PlayerSpeedMultiplier`

Visibility rule:

* Visible if `0 <= timeToHit <= SpawnDistanceWorld / ApproachSpeed`

Position:

* `distance = clamp(ApproachSpeed * timeToHit, 0, SpawnDistanceWorld)`  
* `worldPos = hitWorldPos + ApproachDirection * distance`

Judgement is time-based; speed never affects judgement.

---

## **7\) Input and binding rules (multi-finger)**

### **7.1 Touch tracking**

* Multi-touch supported at runtime.  
* Touches have stable `touchId`.  
* Two states:  
  * **free** touch  
  * **bound** touch (to an active HoldNote)

* **Bound touches may satisfy Flick/Catch** checks (allowed), but remain bound to their Hold; leaving the lane can cause hold ticks to miss.

### **7.2 Tap**

* Triggered by TouchBegin inside lane region.  
* Hittable if within GreatWindowMs.  
* Judged as Perfect/Great/Miss; Perfect+ display if within sub-window.

### **7.3 Flick (lane-relative, event-based)**

* Flick recognition is **event-based**: a `FlickEvent` is produced by the gesture tracker when a touch gesture exceeds distance, velocity, and elapsed-time thresholds (spec §8.3 / §7.3.1).
* **Multiple FlickEvents can occur during one continuous touch** (e.g. U then D without lifting), because the gesture baseline resets immediately after each event is emitted.
* Each Flick note consumes at most one FlickEvent per judgement.
* Requirements for a FlickEvent to match a note:
  * the touch position at event time is inside the note’s lane
  * event time is within `[noteTimeMs − GreatWindowMs, noteTimeMs + GreatWindowMs]`
  * gesture displacement matches the note’s required direction (see §7.3.1)
* Flick direction is **lane-relative** (player-facing-inward frame — see §7.3.1).
* Flick judgement tiers: **Perfect, Great, or Miss** based on timing (same windows as tap). See `FlickPerfectWindowCoversGreatWindow` toggle in §8.3.1 to suppress the Great tier.
* **v0 toggle — `FlickRequireTouchBegin` (default false):** when true, only FlickEvents that completed within `FlickMaxGestureTimeMs` of the original `TouchBegin` are eligible. When false, any active touch can arm a flick; the gesture baseline resets the first time the touch becomes eligible (in-window + in-lane) for each note.

### **7.3.1) Flick recognition details (locked for v0)**

* Flick is recognized from touch movement in **playfield-plane coordinates** (not raw screen pixels).
* A `FlickEvent` captures the gesture displacement, position, and time at the moment thresholds were crossed.
* JudgementEngine matches each FlickEvent to the best note candidate using the event’s time, position, and displacement — not the current frame’s touch state.
* Lane-relative basis is evaluated at **note time** using the lane’s center angle θ (degrees).

#### Flick direction basis vectors (player-facing-inward frame, locked for v0)

Given lane center angle θ:

```
radialOut     = ( cos θ,  sin θ)   outward from arena center
radialIn      = (-cos θ, -sin θ)   inward toward arena center
tangentialCCW = (-sin θ,  cos θ)   counter-clockwise around arena
tangentialCW  = ( sin θ, -cos θ)   clockwise around arena
```

Chart `direction` field → expected basis vector:

| direction | vector | meaning |
|---|---|---|
| `U` | `radialIn` | toward arena center (“up” when facing inward) |
| `D` | `radialOut` | away from arena center (“down” when facing inward) |
| `L` | `tangentialCW` | clockwise (“left” when facing inward) |
| `R` | `tangentialCCW` | counter-clockwise (“right” when facing inward) |

Match threshold: `dot(normalised_displacement, expected) >= cos(45°) ≈ 0.707`.

* Flick “arming” window:
  * The touch must be inside the lane at least once within `[timeMs - GreatWindowMs, timeMs + GreatWindowMs]`.
* Judgement time:
  * Use the moment the gesture crosses thresholds (“recognition time”) to compute Perfect+ display (optional), but judgement label remains Perfect/Miss.

### **7.4 Catch (single note, Perfect-or-Miss)**

* A catch note is satisfied if **any touch** (free or bound) is inside lane at the note time (within window semantics used by implementation).  
* Judgement label: Perfect or Miss only.

### **7.4.1) Catch timing semantics**

* A CatchNote at `timeMs` is judged **Perfect** if any touch is inside the lane at any instant within:  
  * `[timeMs - GreatWindowMs, timeMs + GreatWindowMs]`  
* The catch is consumed the moment it first becomes satisfied inside that window.  
* Otherwise: Miss.

### **7.5 Hold (baked ticks, deterministic)**

* Hold start requires TouchBegin inside lane within GreatWindowMs of `startTimeMs`.  
* Touch becomes **bound** to the hold until release/end.  
* Each baked tick:  
  * if bound touch is down and inside lane at tickTimeMs → Perfect tick  
  * else → Miss tick  
* Early release misses remaining ticks.

### **7.5.1) Hold tick processing across frames (must-have)**

**Goal:** Avoid missing ticks on low FPS / hitches.

* Maintain `prevSongTimeMs`.  
* Each update, process all hold ticks where:  
  * `prevSongTimeMs < tickTimeMs <= currentSongTimeMs`  
* Same pattern applies to consuming time-based notes if you implement consumption via a time window.

### **7.6 Overlap handling (tie-break order, locked)**

A single touch can satisfy at most one judging note at a given instant. When multiple candidates are satisfied simultaneously, select using this stable order:

1) **Smallest absolute timing error** `abs(effectiveChartTimeMs - noteTimeMs)`
2) **Smallest geometric distance to lane centerline** at that time:
   * Use `abs(shortestSignedAngleDeltaDeg(touchDeg, laneCenterDeg))` (degrees) as the primary distance metric.
3) **Higher lane priority** (`lane.priority` larger wins)
4) **Stable fallback:** lower `noteIndex` (earlier in file order)

This tie-break is used for Tap/Flick/Catch and for Hold-start binding.

### **7.7 “Lane-based hit anywhere”**

* Notes are lane-based: any valid touch position **anywhere inside the lane region** counts (no per-note X position).

---

## **8\) UI screens (Minimal Player v0)**

### **8.1 Song Select**

* Enumerate installed `.rpk` packs  
* Show jacket (best size), title, artist  
* Difficulty buttons based on `songinfo.charts[]`  
* Preview playback:  
  * loop between `preview.startTimeMs` and `preview.endTimeMs` if provided

### **8.2 Mode select (v0)**

* Toggle: Standard / Challenger  
* Mode affects judgement windows \+ scoring profile only (chart unchanged).

### **8.3 Settings / Calibration**

* `UserOffsetMs` slider (e.g., \-200..+200)
* Note speed setting:
  * `PlayerSpeedMultiplier`
* Flick thresholds (advanced / optional):
  * Measured in normalized playfield-plane units (see §5.3).
  * Defaults (locked for v0):
    * `minDistanceNorm = 0.03`
    * `minVelocityNormPerSec = 0.8`
    * `maxGestureTimeMs = 120`
  * Expose these as settings sliders, but these defaults must ship in v0.

### **8.3.1 v0 debug/playtest toggles**

These are not persisted PlayerPrefs settings. They are simple static fields in `PlayerSettingsStore`, set in code or via a debug Inspector during playtesting. They do not affect exported charts.

| Toggle | Default | Effect |
|---|---|---|
| `PerfectWindowCoversGreatWindow` | `false` | Extends effective Perfect window to `GreatWindowMs` for **tap and hold**. Great tier is suppressed; every in-window hit scores Perfect (or Perfect+ if within sub-window). Perfect+ sub-window unchanged. Does **not** affect flick. |
| `FlickRequireTouchBegin` | `false` | When true, only FlickEvents completed within `FlickMaxGestureTimeMs` of a new touch (`TouchBegin`) are eligible. When false, any active touch can arm a flick; the gesture baseline resets the first time the touch becomes eligible (in-window + in-lane) for each note. |
| `FlickPerfectWindowCoversGreatWindow` | `false` | **Flick only.** When true, the flick Perfect window expands to `GreatWindowMs`; Great tier is suppressed for flick — every in-window flick scores Perfect (or Perfect+ if within sub-window). Perfect+ sub-window unchanged. When false (default), flick timing evaluates normally (Perfect / Great / Miss). |
| `InputBandExpandInnerNorm` | `0.00` | **Input only.** Expands the arena band inward by `InputBandExpandInnerNorm × minDimLocal` local units for touch arming and judgement. Does not affect visuals or chart geometry. Default 0 = no inner expansion. |
| `InputBandExpandOuterNorm` | `0.03` | **Input only.** Expands the arena band outward by `InputBandExpandOuterNorm × minDimLocal` local units for touch arming and judgement. Accounts for finger imprecision when tapping the outer rim. Default 0.03 = 3 % of `minDimLocal`. Does not affect visuals or chart geometry. |
| `JudgementInsetNorm` | `0.03` | **Visual/skin only.** Insets the judgement ring inside `outerLocal` by `JudgementInsetNorm × minDimLocal`. Notes land and the judgement arc is drawn at this radius. Does not change hit-testing, timing windows, or chart geometry. See §5.8. |
| `VisualOuterExpandNorm` | `0.00` | **Visual/skin only.** Extends the arena mesh/arc outer rim beyond `outerLocal` by `VisualOuterExpandNorm × minDimLocal`. Provides a thick-track look with rim beyond the judgement ring. Default 0 = mesh matches chart `outerLocal`. Does not affect hit-testing or charting. See §5.8. |

### **8.4 Gameplay**

* Load audio \+ chart  
* Render playfield in 3D, animate arenas/lanes/camera by keyframes  
* Multi-touch input  
* Pause/resume/restart  
* **No-fail only** in v0

### **8.5 Results**

* Show:  
  * total score  
  * Perfect+/Perfect/Great/Miss counts  
  * hold tick hit stats  
  * optional early/late breakdown

---

## **9\) Performance and stability requirements (v0)**

* No per-frame allocations on gameplay hot path.  
* Efficient active-window evaluation (don’t iterate all notes each frame).  
* Audio decode/streaming appropriate for OGG on mobile.  
* **Background/foreground rule (locked):** on resume, **restart the song from 0** and reset all judgement state (simplest deterministic v0 behavior).

---

## **10\) Explicit out-of-scope (v0)**

* Accounts / cloud save  
* Story, puzzles, world mode, stamina  
* Events, leaderboards, currency, IAP  
* Anti-cheat  
* Tutorial

---

## **11\) Open items (safe to defer)**

* Exact UI styling/visual identity  
* Pack installation UX (manual import vs in-app file picker)