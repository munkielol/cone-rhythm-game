# **Rhythm Game Player App v0 Specification** 

**Document version:** 0.5 (v0 Claude-ready)
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

* **Hold ticks:** Perfect-or-Miss only (v0); each tick is a scored judgement event.
* **Catch notes:** Perfect-or-Miss only (v0).
* `tickTimesMs` are editor-baked and deterministic; runtime consumes the baked list directly.

**Hold tick scoring rules (locked):**

* Tick Perfect (bound touch inside lane at tick time) → combo +1, +1000 pts.
* Tick Miss (touch outside lane OR early release) → combo reset to 0, +0 pts.
* **First Miss tick immediately fails the hold** (no further ticks are evaluated for that hold).
  The visual body remains dim until `endTimeMs` per §5.7.1.
* Hold-bind events (player pressing the hold START) do **not** affect score or combo.
* Hold START miss (player never bound the hold): scores as one Miss (combo break, 0 pts).
* Hold FINAL RESOLVE (completed or early-released): non-scoring — ticks carry all the weight.
* "No spam" guarantee: exactly one Miss event fires per failed hold regardless of how many
  ticks remain after the failure point.

### **4.5 Scoring profile (v0 locked)**

**Scheme: per-note points** (deterministic; no floating-point normalization).

| Tier | Points |
|---|---|
| Perfect | 1000 |
| Great | 700 |
| Miss | 0 |

`TotalScore = sum of points across all judged notes.`

**Combo rules (locked):**

* Perfect or Great → increment combo; update MaxCombo if new high.
* Miss → reset combo to 0.
* Hold ticks follow the same rules (tick Perfect = combo++; tick Miss = combo reset).
* Hold-bind events (hold START) do not affect combo.

**Hold scoring (locked, updated from previous rev):**

* Hold ticks are the **scoring unit** for holds. Each tick fires one judgement event.
* Hold-bind (pressing the hold start): non-scoring; filtered in `ScoreTracker`.
* Hold final resolve (completed or failed): **non-scoring** — ticks already carried the weight.
  Exception: a hold that was **never started** (never bound) scores as one Miss (§4.4).
* This replaces the prior "score once on final resolve" rule.

**Accuracy formula** — unchanged; valid because `PerfectCount` and `TotalJudgedCount` now
include hold ticks, so the ratio remains meaningful as note density increases.

**Implementation:**

* `ScoreTracker` (`Assets/_Project/Player/Runtime/Scoring/ScoreTracker.cs`).
* Wired in `PlayerAppController.Start()` — no prefab edit required.
* Events:
  * `PlayerAppController.OnJudgement` → tap, catch, flick hits + sweep-misses of non-hold notes.
  * `PlayerAppController.OnHoldTick` → each baked tick result (Perfect or Miss); drives hold scoring.
  * `PlayerAppController.OnHoldResolved` → hold lifecycle (non-scoring except Unbound start miss).
* `PointsPerfect = 1000`, `PointsGreat = 700`, `PointsMiss = 0` are `public const int` on `ScoreTracker`.

**Accuracy display formula (v0):**

```
accuracy% = (Perfect × 1000 + Great × 700) / (TotalJudged × 1000) × 100
```

**Song-end results (v0):**

`PlayerAppController` detects song end when `!AudioSource.isPlaying && chartTimeMs >= lastNoteExpiry`.
Logs one-line summary via `ScoreTracker.LogSummary()`:

```
[Score] ===== Song Complete ===== Score=N  MaxCombo=N  Perfect=N  Great=N  Miss=N  Accuracy=N.NN%
```

Display requirements (spec §8.5):

* Total score
* Perfect+/Perfect/Great/Miss counts
* MaxCombo
* Accuracy %
* (hold tick hit stats deferred to future UI layer)

---

## **5\) Playfield \+ 3D rendering model**

### **5.0 System responsibilities and production layering**

The player scene is organized into four tiers with strict ownership rules:

| Tier | Systems | Rule |
|---|---|---|
| **Gameplay-critical** | `ChartRuntimeEvaluator`, `JudgementEngine`, `ArenaHitTester`, `ScoreTracker`, `PlayfieldTransform` | Must function correctly with all debug components disabled or absent. No dependency on debug scaffolding. |
| **Production visuals** | `PlayfieldFrustumProfile`, `ArenaColliderProvider`, `ArenaBandRenderer`, `LaneGuideRenderer`, `JudgementRingRenderer`, `TapNoteRenderer`, `CatchNoteRenderer`, `FlickNoteRenderer`, `HoldBodyRenderer` | Must be fully usable without `PlayerDebugRenderer` or `PlayerDebugArenaSurface`. Driven by `ChartRuntimeEvaluator` via `PlayerAppController`. |
| **Production feedback** | `TouchFeedbackRenderer` *(planned)*, `JudgementFeedbackRenderer` *(planned)* | Separate from note renderers. Receive touch/judgement events from gameplay systems. Skinnable via `GameplayFeedbackSkinSet` (§5.12). Geometry is derived from the same evaluated lane/arena surface as production visual renderers — not camera-specific approximations. |
| **Debug scaffolding** | `PlayerDebugArenaSurface`, `PlayerDebugRenderer` | **Additive only.** Add overlays and diagnostic readouts on top of production systems. Must not own any behavior required for correct gameplay, input, or visuals. |

**Non-negotiable ownership rules:**
* `PlayfieldFrustumProfile` is the single production source of truth for frustum Z heights (`FrustumHeightInner` / `FrustumHeightOuter`). All production renderers read it directly. `PlayerDebugArenaSurface` may optionally delegate to it but does not own it.
* `ArenaColliderProvider` is the production owner of arena `MeshCollider`s for parallax-correct input (§5.2.1). `PlayerDebugArenaSurface` may provide colliders for debug-mode testing but must not be the only source in a production configuration.
* No production gameplay, visual, or feedback system may have a required Inspector reference to `PlayerDebugArenaSurface` or `PlayerDebugRenderer`.
* Debug is purely additive: removing all debug components must leave gameplay, input, and production visuals fully functional.

### **5.1 True 3D scene**

* Gameplay is rendered in a **true 3D scene**.  
* Lanes/notes are visually represented with depth.

### **5.2 ArcCreate-style input mapping (ray → playfield plane)**

* Touch mapping uses gameplay camera:
  1. ray \= `GameplayCamera.ScreenPointToRay(touchPos)`
  2. intersect with **playfield plane**
  3. convert world hit point → **normalized playfield coords** via `PlayfieldTransform`

### **5.2.1 Optional Visual Surface Raycast (parallax-correct input)**

When the arena surface has physical depth (e.g. a frustum/cone mesh), the flat Z=0 plane
intersection produces a parallax mismatch: the screen position of a visible ring edge does not
map to the same local XY as the flat-plane ray, causing visually-correct taps to miss the
hit band.

**Fix:** cast the screen ray against the actual visible mesh surface, then use only the XY of
the 3D hit in PlayfieldRoot local space.

**Implementation (`PlayerAppController`):**

| Inspector field | Type | Default | Description |
|---|---|---|---|
| `useVisualSurfaceRaycast` | `bool` | `true` | Enables the visual surface raycast path. |
| `visualSurfaceLayerMask` | `LayerMask` | *(assign in Inspector)* | Physics layers containing arena surface collider(s). |
| `visualSurfaceRoot` | `Transform` | *(optional)* | Used only for debug labelling; no gameplay effect. |

**Algorithm per touch (down/held only; not touch-end):**

1. Always compute flat-plane projection via `TryRaycast` → `_debugLastPlaneLocalXY` (for fallback + debug).
2. If `useVisualSurfaceRaycast` is enabled, cast `Physics.Raycast` against `visualSurfaceLayerMask`.
   * On hit: `local3 = playfieldRoot.InverseTransformPoint(hit.point)` → use `(local3.x, local3.y)`.
     `localZ` is discarded — only XY feeds the polar hit-test.
   * Sets `_debugUsedVisualSurface = true`.
3. If physics ray misses (or feature disabled), fall back to flat-plane result.

**Setup (production path — `ArenaColliderProvider`):**

1. Add `ArenaColliderProvider` to a scene GameObject (e.g. `GameplayInput`).
2. Assign `playerAppController` and `frustumProfile` in the Inspector.
3. Set the `ArenaColliderProvider` GameObject's layer to a dedicated physics layer (e.g. `ArenaSurface`) — collider child GOs inherit this layer automatically.
4. Set `visualSurfaceLayerMask` on `PlayerAppController` to include that layer.
5. `ArenaColliderProvider` creates and updates one `MeshCollider` per active arena each frame, matching the frustum geometry used by the production visual renderers. No further setup is required.

**Setup (debug path — `PlayerDebugArenaSurface`):**
`PlayerDebugArenaSurface` also creates `MeshCollider`s on its arena child GOs and can provide arena surface raycasts during development. Assign it to the same physics layer. This is debug scaffolding only — it must not be the sole source of arena colliders in a production build.

**Debug (§8.3.1):**

* `DebugShowInputProjection` in `PlayerSettingsStore` — adds an `[Input]` OnGUI line:
  `[Input] usedVisualSurface=true  plane=(x,y)  surface=(x,y)  delta=0.0210`
* When `usedVisualSurface` is true: grey diamond at plane point; orange line connecting plane→surface.

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

**§5.5.2 — Hit Band (touch hit-testing only):**

The actual radial interval accepted for touch arming and judgement is anchored on `judgementRadiusLocal`.
The inward tolerance is expressed as a **fraction of available inward depth**, not a fixed norm.
The outward tolerance remains an explicit norm.

**Inward bound (coverage-based):**
```
inwardDepthLocal = max(0, judgementRadiusLocal − chartInnerLocal)
hitBandInner     = judgementRadiusLocal − Clamp01(HitBandInnerCoverage01) × inwardDepthLocal
```
* `HitBandInnerCoverage01 = 0.0` → no inward tolerance (hitInner == judgementRadius).
* `HitBandInnerCoverage01 = 1.0` → full depth to `chartInnerLocal`.
* Default `0.35` → 35 % of the available depth inward from the judgement line.

**Outward bound (explicit norm):**
```
hitBandOuter = judgementRadiusLocal + HitBandOuterInsetNorm × minDimLocal
```

**`InputBandExpand*` additive fine-tune on top:**
* `hitInnerLocal = max(hitBandInner − InputBandExpandInnerNorm × minDimLocal, chartInnerLocal)`
* `hitOuterLocal = min(hitBandOuter + InputBandExpandOuterNorm × minDimLocal, visualOuterLocal)`

`hitInnerLocal` is clamped to `chartInnerLocal` (never past the chart inner edge).
`hitOuterLocal` is clamped to `visualOuterLocal` (never past the visual outer rim).

The hit band is used **only** for touch arming and judgement (steps 3–6 when called from `JudgementEngine`).  It does **not** affect:
* Visual geometry (arena arc outlines, note approach markers).
* Chart-authored geometry (`outerRadius`, `bandThickness`).
* The judgement ring position (see §5.8 — `judgementRadiusLocal` is **visual only**).
* Hold tick lane membership (`IsInsideFullLane` for hold tick processing).

Debug arcs for `hitInnerLocal` / `hitOuterLocal` are drawn in green by `PlayerDebugRenderer` when `showHitBandArcs` is true.

See §8.3.1 for the default values of all hit-band and visual parameters.

**Required helper definitions (locked):**
* `normalize360(a)` returns angle in `[0, 360)`.
* `shortestSignedAngleDeltaDeg(a, b)` returns the signed delta from `b` to `a` on the shortest path (range `[-180, +180]`).


### **5.6 Enabled vs opacity**

* `enabled` controls interaction (0/1)  
* `opacity` is visual-only

### **5.7 Note visuals**

#### **5.7.0 Design direction (v0 and target)**

**Note body geometry goal:**
Note bodies for all types (Tap, Catch, Flick, Hold) must be:
* **Lane-width-aware** — width at any approach radius equals `LaneChordWidthAtRadius(r, widthDeg/2) × widthRatio`, computed per-frame from the current evaluated lane geometry.
* **Frustum-conforming** — note head Z is lifted onto the cone/frustum surface via `NoteApproachMath.FrustumZAtRadius`, matching the arena surface mesh geometry.
* **Width-stable under lane animation** — the note head width tracks lane width changes smoothly, without popping.

**Target geometry direction:**
The intended final direction is **exact lane-curve-following**: note head caps follow the arc of the lane at their current radius, forming curved-edge quads rather than straight-chord approximations. This is reached incrementally:
* **v0 step 1 (done):** Single-segment trapezoid. One chord per cap edge. Acceptable for straight or nearly-straight lanes.
* **v0 step 2 (current):** Segmented curved-cap geometry — each cap edge subdivided into N arc-sampled column boundaries (ColumnCount = 5 by default). The note body visibly follows the lane arc at all widths. Implemented in `NoteCapGeometryBuilder` (`Assets/_Project/Player/Runtime/Visuals/NoteCapGeometryBuilder.cs`). All three production renderers (Tap/Catch/Flick) use this builder.
* **v0+ target:** Move `NoteCapGeometryBuilder` to `Assets/_Project/Shared/` so the Chart Editor Playfield Preview can share it (spec §chart_editor §3.3). Increase ColumnCount if very-wide lanes show visible stepping.

**Note skin goal:**
The preferred skin workflow is **texture/PNG-driven, not material-only authoring**. Materials define the shader and rendering template; the primary artistic content is textures assigned to them. The skin system must keep visual identity stable under variable lane width — see §5.7.3 for the full skin philosophy.

**Note body occupancy:**
Notes visually occupy a fraction of the lane width controlled by `noteLaneWidthRatio` in `NoteSkinSet` (default 0.9). This applies to all note types including hold bodies (`holdLaneWidthRatio`).

#### **5.7.a Tap / Catch / Flick note heads — production renderers**

Three separate MonoBehaviours, each handling one note type, driven by a `NoteSkinSet` ScriptableObject:

| Renderer | Script | Note type |
|---|---|---|
| `TapNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/TapNoteRenderer.cs` | `NoteType.Tap` |
| `CatchNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/CatchNoteRenderer.cs` | `NoteType.Catch` |
| `FlickNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/FlickNoteRenderer.cs` | `NoteType.Flick` |

All three use `Graphics.DrawMesh` with a pre-allocated mesh pool (128 slots each). Vertices are overwritten in-place each frame — zero per-frame GC allocation. Current v0 geometry is a 5-column segmented curved-cap (§5.7.0 step 2) built by `NoteCapGeometryBuilder`. Mesh templates have 12 vertices and 10 triangles per note.

**`NoteSkinSet` ScriptableObject** (`Assets/_Project/Player/Runtime/Skins/NoteSkinSet.cs`):

Create via **Assets → Create → RhythmicFlow → Note Skin Set**. Assign to a serialized field on `TapNoteRenderer`, `CatchNoteRenderer`, and `FlickNoteRenderer` in the Inspector.

The intended authoring workflow is: **import PNG → assign to NoteSkinSet → result appears in-game.** Do not require creating a unique `Material` asset per skin variant. The material is a shared shader template; the texture is the primary authoring artifact.

**Body rendering — shared material + per-type textures:**

| Field | Type | Description |
|---|---|---|
| `noteBodyMaterial` | `Material` | Shared shader template for all Tap/Catch/Flick note bodies. Must support `_MainTex` and `_Color` via `MaterialPropertyBlock`. A simple Unlit/Transparent shader is sufficient. Do not bake the texture into this asset — it is assigned per draw call. |
| `tapBodyTexture` | `Texture2D` | Body texture for Tap notes. Assigned to `_MainTex` via `MaterialPropertyBlock` at draw time. |
| `catchBodyTexture` | `Texture2D` | Body texture for Catch notes. |
| `flickBodyTexture` | `Texture2D` | Body texture for Flick notes. |
| `fallbackBodyTexture` | `Texture2D` (optional) | Used when a type-specific texture is null. Renderers log a one-time warning when falling back. |

**Body skin layout — fixed edge + tiled center:**

| Field | Type | Description |
|---|---|---|
| `bodyLeftEdgeU` | `float` [0..0.5] | Left decorative border width as a fraction of texture width. The leftmost `bodyLeftEdgeU` of the UV range is reserved for the fixed left edge and never distorted by lane width changes. Default 0.1. |
| `bodyRightEdgeU` | `float` [0..0.5] | Right decorative border width as a fraction of texture width. The rightmost `bodyRightEdgeU` of the UV range is reserved for the fixed right edge. Default 0.1. |
| `bodyCenterTileRatePerUnit` | `float` | How many times the center UV region tiles per PlayfieldLocal unit of note chord width. Higher = finer center pattern. Default 1.0. |

**Flick arrow overlays (Flick only — separate from body):**

| Field | Type | Description |
|---|---|---|
| `flickArrowMaterialUp` | `Material` | Arrow quad material — Up direction |
| `flickArrowMaterialDown` | `Material` (optional) | Arrow quad — Down; falls back to Up (rotated 180°) |
| `flickArrowMaterialLeft` | `Material` | Arrow quad — Left direction |
| `flickArrowMaterialRight` | `Material` (optional) | Arrow quad — Right; falls back to Left (rotated 180°) |

**Geometry and state parameters:**

| Field | Type | Description |
|---|---|---|
| `noteLaneWidthRatio` | `float` [0.1–1] | Head width as fraction of lane angular span. Default 0.9 |
| `noteRadialHalfThicknessLocal` | `float` | Radial half-thickness of note head. Default 0.022 |
| `arrowSizeLocal` | `float` | Constant arrow size — does **not** scale with lane width. Default 0.08 |
| `arrowSurfaceOffsetLocal` | `float` | Z offset to lift arrow above note head. Default 0.003 |
| `missedTintColor` | `Color` | `_Color` tint via `MaterialPropertyBlock` on missed notes. Default `(0.4, 0.4, 0.4, 0.55)` |

**Geometry parameters (per-renderer Inspector fields):**

| Parameter | Default | Notes |
|---|---|---|
| `noteLeadTimeMs` | 2000 ms | Must match `HoldBodyRenderer` |
| `spawnRadiusFactor` | 0 | Spawn at inner arc edge (v0 default) |
| `frustumProfile` | *(assign in Inspector)* | `PlayfieldFrustumProfile` — shared production source for frustum Z heights. When assigned and `UseFrustumProfile` is true, note heads are lifted onto the cone surface via `FrustumZAtRadius`. When null, falls back to `surfaceOffsetLocal`. |
| `surfaceOffsetLocal` | 0.002 | Flat Z offset (local units) used as fallback when `frustumProfile` is not assigned or is disabled. |

**Scene wiring (manual):** Add `TapNoteRenderer`, `CatchNoteRenderer`, and `FlickNoteRenderer` as components to a suitable scene GameObject (e.g. `GameplayRenderers`). Assign the `PlayerAppController` reference and a `NoteSkinSet` in the Inspector for each. There is no runtime auto-creation; a missing `PlayerAppController` or `NoteSkinSet` logs a one-time warning and skips rendering for that renderer.

**Visibility rules:**
* Hidden until `timeToHit ≤ noteLeadTimeMs`.
* Hidden once `State == Hit`.
* Missed notes remain visible (dimmed by `missedTintColor`) until `timeToHit < −greatWindowMs`.
* Hold notes are excluded — rendered by `HoldBodyRenderer`.

**Transitional renderer (debug/prototyping only):**
`NoteApproachRenderer` (`Assets/_Project/Player/Runtime/Visuals/NoteApproachRenderer.cs`) is a minimal single-material renderer (one `noteHeadMaterial` + per-type color tint). It predates the skin system and is retained for prototyping and debug comparison only. It is **not** the production rendering path and should not be used as the basis for new visual work.

### **5.7.1 Hold body rendering — scroll / long-note style (v0)**

A hold note is rendered as a **ribbon** that scrolls toward the judgement line, exactly like an ArcCreate long note.

**Approach formula (canonical, same as tap/flick):**

```
timeToHitMs = eventTimeMs − chartTimeMs
alpha       = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
r           = Lerp( spawnR, judgementR, alpha )

spawnR      = innerLocal + spawnRadiusFactor × (judgementR − innerLocal)
judgementR  = outerLocal − JudgementInsetNorm × minDimLocal
```

`alpha = 1` (pinned at `judgementR`) when `timeToHitMs ≤ 0` — no special branch needed.

**v0 default — spawn at inner arc:** `spawnRadiusFactor = 0` → `spawnR = innerLocal`. Hold tails first appear at the inner band edge and travel outward to `judgementR`. Values `> 0` move the spawn point inward along the approach path (useful for debug/tuning).

**`noteLeadTimeMs` must match `PlayerDebugRenderer.noteLeadTimeMs` (default 2000 ms):**

The scheduler activates notes `ActivationLeadMs = 5000 ms` before `startTimeMs` (so judgement input is never missed). The visual lead time (`noteLeadTimeMs`) is a separate, shorter window — the last N ms of that activation window where the ribbon is actually drawn.

The invariant that guarantees `alpha = 0` (ribbon at `innerLocal`) on the first drawn frame is:

```
noteLeadTimeMs  ≤  ActivationLeadMs
alpha on first visible frame = 1 − Clamp01(noteLeadTimeMs / noteLeadTimeMs) = 0  ✓
```

If `noteLeadTimeMs > noteLeadTimeMs_of_debug_renderer`, holds appear mid-approach at song start whenever `startTimeMs − t₀ < noteLeadTimeMs`. **This was the v0 bug (linter set it to 5000; correct value is 2000).**

**Three phases:**

| Phase | chartTime | headR | tailR |
|---|---|---|---|
| **Before start** | `< startTimeMs` | `ComputeApproachR(startTimeMs)` | `ComputeApproachR(endTimeMs)` |
| **During hold** | `≥ startTimeMs, < endTimeMs` | `judgementR` (pinned by formula) | `ComputeApproachR(endTimeMs)` |
| **After end** | `≥ endTimeMs + greatWindowMs` | — hidden — | — hidden — |

The ribbon spans `[tailR → headR]` radially along `lane.CenterDeg`. As `chartTime` advances through the hold, the head is pinned and the tail catches up — the ribbon shrinks until it disappears.

**Ribbon geometry (v0 — single-segment trapezoid):**

* Direction: current `lane.CenterDeg` each frame (follows animated lane — no bending in v0).
* Width: the ribbon is a **trapezoid** — head and tail have different widths because they sit at different radii. Lane borders are radial lines at `centerDeg ± widthDeg/2`; the chord between them at radius `r` is:
  ```
  width(r) = 2 · r · sin( widthDeg/2 · Deg2Rad ) · holdLaneWidthRatio
  widthHead = width(headR)   (wider — farther from center)
  widthTail = width(tailR)   (narrower — closer to center)
  ```
  `holdLaneWidthRatio` is a skin parameter (default 0.7; 1.0 = exact lane border width).
* Vertex layout in PlayfieldRoot local space:
  ```
  tailLeft  = tailCenter − tangLocal × (widthTail / 2)
  tailRight = tailCenter + tangLocal × (widthTail / 2)
  headRight = headCenter + tangLocal × (widthHead / 2)
  headLeft  = headCenter − tangLocal × (widthHead / 2)
  ```
* Drawn via `Graphics.DrawMesh` — vertices written in-place into a pooled `Mesh` (no per-note GameObject, no per-frame GC allocation).

**Target ribbon skin direction (later step — not part of initial Tap/Catch/Flick skin implementation):**
Hold body skins must follow the same philosophy as note head skins (§5.7.3). This migration is a separate later step; do not block Tap/Catch/Flick skin work on it:
* **Decorative side borders** — fixed UV-mapped edge regions that preserve their art regardless of lane width changes. Must not stretch.
* **Tiled center (width)** — the center region tiles between the borders as lane width changes. Full-surface stretch is not acceptable as a final result.
* **Tiled length (radial)** — the ribbon texture tiles along the radial direction at a consistent rate. Stretch-based length mapping is not acceptable as a final result; it smears art on long holds.
* The current v0 implementation uses flat color passes driven by `MaterialPropertyBlock._Color` per state. This is a transitional placeholder only — not the intended final hold rendering path.

**Visibility and missed-hold lifetime (v0 locked):**

Judging eligibility and visual lifetime are **decoupled**:

| Condition | Action |
|---|---|
| `State == Hit` (successfully completed) | Stop drawing immediately |
| `startTimeMs − chartTime > noteLeadTimeMs` | Not yet on screen — do not draw |
| `endTimeMs − chartTime < −greatWindowMs` | Past end window — stop drawing |
| `State == Missed` OR `HoldBind == Finished` (released early) | **Keep drawing** with dim `holdColorReleased` until `endTimeMs` |

**Missed-hold geometry:** when a hold is missed or released early, the body continues to shrink geometrically toward `judgementR`. The head pins at `judgementR` (naturally, via `Clamp01` on negative `headToHit`). The tail continues approaching; at `endTimeMs` the tail also reaches `judgementR`, making the ribbon degenerate (zero area) and it disappears exactly then — no special branch needed.

**Color / state mapping (v0):**

| `NoteState` | `HoldBindState` | Color |
|---|---|---|
| `Active` | `Unbound` | `holdColorApproaching` — approaching, not yet hittable |
| `Active` | `Bound` | `holdColorActive` — being held, ticks scoring |
| `Active` | `Finished` | `holdColorReleased` — released early, pre-sweep |
| `Missed` | any | `holdColorReleased` — missed start or swept after early release |
| `Hit` | `Finished` | (not rendered — successfully completed) |

**Implementation:** `HoldBodyRenderer` (`Assets/_Project/Player/Runtime/Visuals/HoldBodyRenderer.cs`).
Approach radius, frustum Z, and lane chord width computations delegate to `NoteApproachMath` (Shared).

### **5.7.2 Flick arrow billboard (v0)**

Each `FlickNoteRenderer` draws a second quad per flick note: a camera-facing direction arrow.

**Direction convention:**
The arrow points in the **gesture direction** — the same direction the player is expected to swipe (consistent with the flick basis vectors in §7.3.1). The texture's `+Y` axis aligns with the gesture direction vector in PlayfieldRoot local XY:

| FlickDirection | Gesture meaning (§7.3.1) | dir2DLocal in PlayfieldRoot XY |
|---|---|---|
| `"U"` (Up) | Toward arena center | `(−cosθ, −sinθ)` — radial inward |
| `"D"` (Down) | Away from arena center | `(cosθ, sinθ)` — radial outward |
| `"L"` (Left) | Clockwise tangential | `(sinθ, −cosθ)` — CW tangent |
| `"R"` (Right) | Counter-clockwise tangential | `(−sinθ, cosθ)` — CCW tangent |

where `θ` = `laneCenterDeg` in radians.

> **Implementation note (v0 transitional):** The current `FlickNoteRenderer.cs` has the U/D directions inverted relative to this table (U renders as radialOut, D as radialIn) and L/R tangent directions swapped. This is a known divergence — the spec above is the authoritative target. Align the renderer's `dir2DLocal` switch block in a follow-up task.

**Arrow size:** `arrowSizeLocal` is constant — it does **not** scale with lane width. Flick arrows are readability elements, not lane-body elements; scaling them with narrow lanes would make them unreadable.

**Up/Down visual family:** Up and Down arrows may share one material/texture and differ only by 180° rotation (achieved automatically by the billboard matrix). Left and Right may use distinct assets or also share-with-rotation.

**Billboard construction:**

A single unit-square mesh (`_arrowMesh`) is shared by all simultaneous arrows — shape never changes. Per-note orientation is encoded entirely in the `Matrix4x4` passed to `Graphics.DrawMesh`:

```
arrowUp    = pfRoot.TransformDirection(dir2DLocal)          // world
arrowRight = Cross(arrowUp, −camForward).normalize          // world
arrowNorm  = Cross(arrowRight, arrowUp)                     // world (faces camera)

matrix = TRS(
  pos:   pfRoot.TransformPoint(noteCenter + Z*surfaceOffset),
  rot:   LookRotation(forward=arrowNorm, up=arrowUp),
  scale: (arrowSizeLocal, arrowSizeLocal, 1)
)
```

This requires zero per-frame mesh allocation and no arrow-specific pool.

### **5.7.3 Note skin philosophy (v0 — implemented)**

The note skin system uses **texture/PNG-driven authoring**. Materials are shader templates. The primary authoring artifacts are textures. Replacing the look of a note type means swapping its texture in `NoteSkinSet`, not writing a new shader.

**Fixed-edge + tiled-center body layout:**

Note body textures are laid out in three regions along the note width axis:

```
[left-border | tiled-center | right-border]
← fixed UV  →← tiles with width →← fixed UV →
```

* **Decorative border regions** — fixed UV-space width defined by `bodyLeftEdgeU` / `bodyRightEdgeU` in `NoteSkinSet`. Contain art (line detail, glow, symbol). Must not stretch or compress as lane width changes.
* **Tiled center region** — occupies the UV range between the two border regions. Tiles horizontally at rate `bodyCenterTileRatePerUnit` per PlayfieldLocal unit of chord width. **Tiling is required** — full-surface stretch is not acceptable as a final result; it causes visible art distortion when lane width animates.

As lanes animate narrower or wider:
* Border art remains stable — UV-mapped to fixed texcoord regions.
* Center region tiles in or out — the number of tile repetitions tracks the current note chord width.
* The overall note stays visually clean at any animated width.

**Radial (length) axis for hold bodies:** The hold ribbon texture tiles along the radial direction at a consistent rate. Full-length stretch is not acceptable as a final result; it smears art on long holds.

**Implementation approach — CPU-driven per-frame UV assignment (v0):**

The initial implementation is **CPU-driven**: the renderer computes edge-aware UVs for the curved-cap mesh each frame and writes them into a pre-allocated `Vector2[]` scratch buffer, then assigns `mesh.uv = _uvScratch`. This is the same pattern used for per-frame vertex positions.

This approach is chosen because:
* **Correctness first** — UV mapping is geometrically tied to the actual arc-sampled column widths, which vary with lane animation. CPU-side computation is the most direct way to keep UVs accurate.
* **Debuggable** — per-frame UV values can be inspected directly; shader-side parametric tiling is harder to verify against geometry.
* **Parity with curved geometry** — the 5-column segmented cap has irregular column widths near the arc edges; CPU assignment handles this naturally.
* **Safe iteration** — note meshes are small (12 vertices); per-frame UV writes add negligible cost for v0 note counts. Optimization should be profiling-driven, not speculative.

**Shader-side tiling is deferred.** A shader-optimized path (e.g. using a custom tiling shader that receives edge fractions and tile rate as material properties and handles center tiling in the fragment shader) may be added in a future pass. When added, it must be compatible with the same `NoteSkinSet` authoring data (`bodyLeftEdgeU`, `bodyRightEdgeU`, `bodyCenterTileRatePerUnit`) — the data contract must not change.

**Implementation status:**

| Step | Status | Description |
|---|---|---|
| Step 1 | done | Single-segment trapezoid, flat `_Color` tinting only |
| Step 2 | done | Segmented curved-cap geometry (NoteCapGeometryBuilder) |
| Step 3 | **current** | CPU-driven per-frame UV with fixed-edge + tiled-center (this spec) |
| Step 4 | future | Hold body skin migration to same fixed-edge + tiled-center philosophy |
| Step 5 | future | Optional shader-side tiling optimization (same authoring data; no NoteSkinSet changes) |

**Hold body skin direction:** Hold will follow the same fixed-edge + tiled-center philosophy, with an additional tiling requirement along the radial (length) direction. Hold migration is a **later step** and is explicitly not part of the Tap/Catch/Flick initial skin implementation.

The skin geometry rules described above apply equally to the Chart Editor Playfield Preview (§chart_editor §3.3).

### **5.8) Judgement line / hit indicator (v0)**

**Goal:** Provide a strong visual anchor for timing (Arcaea-style inset judgement ring).

The judgement ring sits **inside** the chart outer radius.  The outer rim remains visible beyond it for aesthetics and finger comfort.

**Five radii (all in PlayfieldLocal units):**

| Radius | Formula | Purpose |
|---|---|---|
| `innerLocal` | `outerLocal − bandLocal` | Chart inner edge; hit-testing clamp. |
| `hitInnerLocal` | `max(judgement − Clamp01(HitBandInnerCoverage01) × inwardDepthLocal − InputBandExpandInner × minDim, innerLocal)` | Input-only inner edge of the hit band (see §5.5.2). |
| `judgementRadiusLocal` | `outerLocal − JudgementInsetNorm × minDimLocal` | Where notes land visually; where the judgement arc is drawn. **Visual only.** |
| `hitOuterLocal` | `min(judgement + (HitBandOuter + InputBandExpandOuter) × minDim, visualOuterLocal)` | Input-only outer edge of the hit band (see §5.5.2). |
| `visualOuterLocal` | `outerLocal + VisualOuterExpandNorm × minDimLocal` | Visual mesh / arc outer rim. **Visual only.** Default: `outerLocal` (no expansion). |

* **Judgement Arc**: thin, clearly visible arc at `judgementRadiusLocal` per arena (purple-white by default in debug renderer).
* **Note approach**: notes travel from `spawnR` (near inner edge) toward `judgementRadiusLocal`; alpha reaches 1.0 at the judgement ring, not at the chart outer edge.
* **Visual outer rim**: arena surface mesh and outer arc line extend to `visualOuterLocal` so track looks thick beyond the judgement ring. Does not affect hit-testing.
* **Hit-testing uses the hit band**: spatial lane membership uses `[hitInnerLocal, hitOuterLocal]` (see §5.5.2).  `judgementRadiusLocal` and `visualOuterLocal` are **visual only**.
* The Judgement Arc is always visible during gameplay (unless the arena is disabled).
* Debug hit-band arcs (green, drawn by `PlayerDebugRenderer`) show `hitInnerLocal` and `hitOuterLocal` per arena.

**Production implementation:** `JudgementRingRenderer` (`Assets/_Project/Player/Runtime/Visuals/JudgementRingRenderer.cs`).
Draws a thin arc-strip mesh at `judgementR` per enabled arena each `LateUpdate`.
Reads enabled state and arc span from `ChartRuntimeEvaluator` (via `PlayerAppController.Evaluator`).

| Parameter | Default | Notes |
|---|---|---|
| `ringHalfThicknessLocal` | 0.008 | Radial half-thickness in PlayfieldLocal units |
| `ringSegments` | 32 | Segments per full 360°; partial arcs use proportionally fewer |
| `ringColor` | `(0.85, 0.75, 1, 0.9)` | Purple-white default |

**Production playfield visual components (complete list):**

All of the following work without `PlayerDebugRenderer` or `PlayerDebugArenaSurface` and must be usable in a production build:

| Component | Script | Responsibility |
|---|---|---|
| `ArenaSurfaceRenderer` | `Assets/_Project/Player/Runtime/Visuals/ArenaSurfaceRenderer.cs` | Filled annular sector per arena — base, detail, and accent layer passes. Skinned via `ArenaSurfaceSkinSet` (§5.8.1). |
| `JudgementRingRenderer` | `Assets/_Project/Player/Runtime/Visuals/JudgementRingRenderer.cs` | Arc strip at `judgementR` per arena |
| `ArenaBandRenderer` | `Assets/_Project/Player/Runtime/Visuals/ArenaBandRenderer.cs` | Outer + inner arc strip outlines per arena |
| `LaneGuideRenderer` | `Assets/_Project/Player/Runtime/Visuals/LaneGuideRenderer.cs` | Left/center/right radial guide lines per lane |
| `TapNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/TapNoteRenderer.cs` | Tap note bodies |
| `CatchNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/CatchNoteRenderer.cs` | Catch note bodies |
| `FlickNoteRenderer` | `Assets/_Project/Player/Runtime/Visuals/FlickNoteRenderer.cs` | Flick note bodies + arrow overlays |
| `HoldBodyRenderer` | `Assets/_Project/Player/Runtime/Visuals/HoldBodyRenderer.cs` | Hold ribbon bodies |

**Production frustum and collider components:**

| Component | Script | Responsibility |
|---|---|---|
| `PlayfieldFrustumProfile` | `Assets/_Project/Player/Runtime/Visuals/PlayfieldFrustumProfile.cs` | Single production source of truth for `FrustumHeightInner` / `FrustumHeightOuter`. All production renderers read this directly. |
| `ArenaColliderProvider` | `Assets/_Project/Player/Runtime/Playfield/ArenaColliderProvider.cs` | Production owner of arena `MeshCollider`s for parallax-correct visual-surface raycasts (§5.2.1). Matches the frustum geometry of the visual renderers. |

**Debug scaffolding (additive only — `PlayerDebugArenaSurface` and `PlayerDebugRenderer`):**

`PlayerDebugArenaSurface` and `PlayerDebugRenderer` consume the same evaluated geometry as production systems and add overlays on top. They must not be required for any production behavior.

* `PlayerDebugArenaSurface` rebuilds the grey cone/frustum sector mesh per arena each
  `LateUpdate` when any geometry parameter (outerRadius, bandThickness, arcStartDeg,
  arcSweepDeg, center, frustum heights) changes past an epsilon threshold.  Uses
  `ChartRuntimeEvaluator` (via `playerAppController.Evaluator`) as the data source so
  all arenas — including disabled ones — can be shown/hidden by `EnabledBool` (spec §5.6).
  Disabling then re-enabling the component mid-song causes meshes to snap to the current
  evaluated state immediately (watermarks are reset in `OnEnable`).

  **Debug visibility toggles (Inspector, `PlayerDebugArenaSurface`):**

  | Toggle | Default | Effect |
  |---|---|---|
  | `forceHideArenaSurfaceMesh` | `false` | Hides the grey fill mesh (`MeshRenderer.enabled = false` per arena child) while evaluation, mesh updates, and debug line overlays continue running. Use this to see lane/hitband debug lines without the opaque surface obscuring them. Does **not** affect chart evaluation or hit-testing. |
  | `forceDisableArenaSurfaceCollider` | `false` | Disables the `MeshCollider` (`Collider.enabled = false`) so parallax-correct visual-surface raycasts (spec §5.2.1) do not hit the arena mesh. Leave false when testing the visual-surface input path. |

  Both toggles work by controlling `Renderer.enabled` / `Collider.enabled` on arena child GOs.
  Child GOs are **never** disabled via `SetActive` — that would conflict with per-frame evaluation.
  `MeshCollider.sharedMesh` is only assigned after the first valid mesh build (`HasValidGeometry = true`);
  assigning a zero-vertex placeholder to PhysX causes "cleaning the mesh failed" warnings.

  `PlayerDebugArenaSurface` may optionally read from a `PlayfieldFrustumProfile` when one is assigned in the Inspector. When assigned, its public `UseFrustumProfile` / `FrustumHeightInner` / `FrustumHeightOuter` getters delegate to the profile. This keeps the debug surface in sync with the production frustum without owning it.

* `PlayerDebugRenderer.UpdateArenaLineRenderers()` repositions the arena arc outlines
  [outer arc, inner arc, start/end rays, judgement ring] every frame from the current
  `DebugArenaGeometries`, exactly as `UpdateLaneLineRenderers()` does for lanes.

**Evaluated-geometry snapshot readout (`PlayerDebugRenderer`, debug-only):**

`PlayerDebugRenderer` contains a compact once-per-second text overlay (bottom-left of the
Game view) that prints the evaluated geometry values at the current `effectiveChartTimeMs`.
Toggle `debugShowEvaluatedGeometrySnapshot` in the Inspector to enable it.  It does **not**
require `DebugShowTouchBand` to be on.

| Inspector field | Default | Effect |
|---|---|---|
| `debugShowEvaluatedGeometrySnapshot` | `false` | Enables the snapshot overlay in the bottom-left of the Game view. |
| `debugSnapshotIntervalSeconds` | `1.0` | How often (seconds) the snapshot text is rebuilt. Clamped to ≥ 0.1 s. |

Snapshot content (one rebuild per interval; cached `string` read every `OnGUI` frame):
```
--- EvalSnapshot @ 1234ms ---
Arena[arena-a]:  arcStart=350.12°  sweep=60.00°  |  outer=0.412L  inner=0.212L  jdg=0.402L
Frustum:  zInner=0.001  zOuter=0.150          ← only when PlayfieldFrustumProfile is assigned
Lane[lane-1]:  center=359.50°  width=20.00°  |  left=349.50°  right=9.50°
```

Data sources — all values come from the same single-source-of-truth path as gameplay:
* **Arena fields**: `ChartRuntimeEvaluator.GetArena(0)` + `PlayfieldTransform.NormRadiusToLocal`.
* **`jdg` (judgementRadiusLocal)**: `ArenaHitTester.ComputeHitBandLocal` — identical formula to `JudgementRingRenderer`, `UpdateHitBandArcs`, and `JudgementEngine`.
* **Frustum Z**: `PlayfieldFrustumProfile.FrustumHeightInner/Outer` (visual only; hidden when `frustumProfile` is not wired in the Inspector).
* **Lane fields**: `ChartRuntimeEvaluator.TryGetLane("lane-1", ...)`, falling back to index 0.

Allocation policy: `_snapshotSb` (`StringBuilder`, capacity 512) is pre-allocated in `Awake`
and reused via `Length = 0` on each rebuild.  The only GC allocation per interval is the
final `StringBuilder.ToString()` call.  **No per-frame heap allocation.**

Use this overlay to verify:
* `arcStartDeg` / lane `centerDeg` animate smoothly on wrap-torture charts (e.g. 350→10→350°).
* `outerLocal` / `jdg` change over time on radius-breathing charts.
* The snapshot text does **not** update every frame (watch the timestamp — it advances in ≥ 1 s steps).

**Judgement ring radius — single source of truth:** All of the following compute
`judgementR = outerLocal − JudgementInsetNorm × minDimLocal` via the same path:
* `JudgementRingRenderer` → `NoteApproachMath.JudgementRadius()`
* `PlayerDebugRenderer` arena ring [4] + lane arc [4] → `ArenaHitTester.ComputeHitBandLocal()`
* `JudgementEngine` hit-band → `ArenaHitTester.ComputeHitBandLocal()`

Lane judgement arcs are exact **subsets** of the arena ring: same radius, narrower arc span
(each lane's `centerDeg ± widthDeg/2`).  When the arena animates, both the full arena ring
and all its lane sub-arcs update in sync.

---

### **5.8.1 Arena surface authoring (ArenaSurfaceSkinSet)**

`ArenaSurfaceSkinSet` controls **appearance only**: materials, textures, colors, opacity, UV tiling, and UV scroll animation.  It does **not** own geometry, colliders, or frustum shape — those remain with `PlayfieldFrustumProfile`, `ArenaColliderProvider`, and `PlayerDebugArenaSurface` respectively.

`ArenaSurfaceRenderer` consumes the skin and renders the filled annular sector for each active arena as three independent draw passes per frame: **base → detail → accent**.  Each layer is completely optional except base (a missing or disabled base layer silently skips all rendering for that frame).

---

#### Layer purposes (artist-facing)

| Layer | Role | Required? | Typical opacity |
|---|---|---|---|
| **Base** | Primary fill of the arena sector — the main surface colour or texture. The visual foundation that all other layers composite on top of. | Yes (surface won't render without it) | 0.5 – 1.0 |
| **Detail** | Secondary fine-pattern layer on top of base — grids, scanlines, subtle noise, hex tiles. Adds surface texture without changing the fundamental colour read. | No | 0.15 – 0.45 |
| **Accent** | Top-most highlight, glow, or animated energy layer. Rim light effects, flowing energy, edge shimmer. Usually the most transparent layer and the one most likely to use `uvScrollSpeed`. | No | 0.05 – 0.35 |

---

#### Texture conventions per layer

**Base layer**

* **Typical content:** Solid colour (no texture), dark panel fill, subtle radial gradient, or low-contrast noise.
* **Aspect ratio:** Square (1:1) preferred for tileable content; any aspect works for solid-colour-only (texture = null).
* **Alpha usage:** Use the alpha channel for soft edge fades or translucent looks.  Combine with `tint.a × opacity` for full control.
* **Pattern types:** Solid colour, subtle gradient, dark ambient noise.
* **Solid-colour-only look:** Leave `texture = null` and set `tint` to the desired colour.  No texture required.

**Detail layer**

* **Typical content:** Seamlessly tileable fine patterns — grid lines, hex tiles, scanlines, soft noise.
* **Aspect ratio:** Square (1:1) strongly recommended so the pattern tiles evenly in both U and V.
* **Alpha usage:** Design the texture with a black-or-transparent background so it composites additively over the base.  Use low `opacity` (0.15–0.45) to keep detail subtle.
* **Pattern types:** Grid/line textures, hex patterns, soft Perlin noise, subtle film grain.
* **Solid-colour-only look:** Not typical for the detail layer — if no fine pattern is needed, leave the layer disabled.

**Accent layer**

* **Typical content:** Glows, rim highlights, flowing energy streaks, or animated shimmer.
* **Aspect ratio:** Wide (2:1 or 4:1) works well for horizontal scroll effects; square for omnidirectional glow/noise.
* **Alpha usage:** High transparency — alpha channel drives the glow shape.  Set `opacity` low (0.1–0.35) and use additive or transparent blending in the material.
* **Pattern types:** Radial glow rings, horizontal/vertical streak textures, animated noise.
* **Solid-colour-only look:** Set `texture = null` and use `tint` with low alpha for a tinted glow band.

**General texture rules (all layers)**

* All textures must be authored as seamlessly tileable when `uvScale` is set above (1, 1) or `uvScrollSpeed != (0, 0)`.
* Keep texture resolution modest — arena surfaces are semi-transparent fills, not hero art.  256×256 or 512×512 is usually sufficient.
* Do **not** bake the tint colour into the texture.  Keep textures greyscale or neutral-tinted so the artist can re-tint freely via `tint` in the Inspector without re-exporting the texture.

---

#### Repeat vs Clamp rules

These rules are mechanically tied to how `uvScale` and `uvScrollSpeed` interact with the shader's `_MainTex_ST` tiling.

| Condition | Required wrap mode | Reason |
|---|---|---|
| `uvScale.x > 1` or `uvScale.y > 1` | **Repeat** | The texture tiles multiple times across the surface.  Clamp would leave most of the surface showing only the border texel. |
| `uvScrollSpeed != (0, 0)` | **Repeat** | The UV offset increases each frame.  Without Repeat, the texture stops at the edge and the majority of the surface shows the clamped edge colour. |
| `uvScale == (1, 1)` **and** `uvScrollSpeed == (0, 0)` | Either; prefer **Clamp** for non-tileable art | The texture maps once with no scrolling.  Clamp prevents artefacts at seams if the texture is not seamless. |
| `uvScale < (1, 1)` (zoom-in / texture fills less than the surface) | **Clamp** | Tiles would appear at the edges if Repeat is used.  Clamp fills the remainder with the border colour. |

**Practical default:** set the texture wrap mode to **Repeat** for any layer whose `uvScale` will be tuned above 1 in a skin set, or whose `uvScrollSpeed` is non-zero.  It is safe to always use Repeat on detail and accent textures since they are almost always tiled.

---

#### Material template conventions

* One `Unlit/Transparent` material is sufficient for all three layers if they share the same shader.  Sharing a single material across layers is allowed — `ArenaSurfaceRenderer` assigns colour and texture per draw call via `MaterialPropertyBlock`, so the material itself remains unmodified at runtime.
* Do **not** bake a texture into the material's Inspector slots.  The renderer sets `_MainTex` via `MaterialPropertyBlock` at draw time.  Baking a texture into the material will be overridden at runtime when a layer texture is assigned.
* If a layer has **no texture** (`texture = null`), the renderer does not set `_MainTex` — the material's default white texture is used and the final colour is `tint × opacity × surfaceOpacityMultiplier`.
* The shader must declare `_MainTex_ST` to receive UV tiling and scroll (`xy = scale, zw = offset`).  Standard Unity built-in shaders (`Unlit/Transparent`, `Sprites/Default`) support this automatically.
* For additive accent blending, use a separate `Unlit/Additive` or `Particles/Additive` material on the accent layer while keeping base and detail on `Unlit/Transparent`.

---

#### Artist workflow

1. **Create the skin asset**
   `Assets → Create → RhythmicFlow → Arena Surface Skin Set`
   Name it descriptively (e.g. `DefaultArenaSurface`, `NeonArenaSurface`).

2. **Create material template(s)**
   In the Project window: `Right-click → Create → Material`.
   Assign `Unlit/Transparent` (or equivalent semi-transparent shader) as the shader.
   Do not assign a texture in the material Inspector — textures are assigned in the skin set.

3. **Configure the base layer (required)**
   In the skin asset Inspector:
   * Set `baseLayer.enabled = true`.
   * Assign the material to `baseLayer.material`.
   * Set `baseLayer.tint` to the desired surface colour (alpha sets translucency).
   * Optionally assign a texture to `baseLayer.texture`.
   * Set `baseLayer.uvScale` to `(1, 1)` initially; increase to tile.
   * Leave `baseLayer.uvScrollSpeed` at `(0, 0)` for a static surface.

4. **Optionally configure detail and accent layers**
   * Enable the layer (`enabled = true`).
   * Assign a material.
   * Assign a tileable texture.
   * Set `uvScale` to tile finely (e.g. `(4, 4)` for detail) or scroll slowly (e.g. `uvScrollSpeed = (0, 0.05)` for accent).
   * Keep `opacity` low (0.2–0.4 for detail, 0.1–0.3 for accent).

5. **Assign the skin set to the renderer**
   Select the `ArenaSurfaceRenderer` component in the scene and drag the skin asset into the `Skin Set` field.

6. **Test in Play Mode with debug off**
   * Disable `PlayerDebugArenaSurface` (or set `forceHideArenaSurfaceMesh = true`) so the grey debug fill does not obscure the production surface layers.
   * Enter Play Mode and load a chart.
   * All three layers should be visible in render order (base, then detail on top, then accent on top of detail).

7. **Iterate safely**
   * All `ArenaSurfaceSkinSet` fields are hot-reloadable in Play Mode via the Inspector — change `tint`, `opacity`, `uvScale`, or `uvScrollSpeed` and the renderer picks them up on the next `LateUpdate`.
   * Enabling or disabling a layer takes effect immediately without leaving Play Mode.
   * The `surfaceOpacityMultiplier` global slider fades the entire surface in/out for quick tuning.
   * If a layer is enabled but its `material` field is empty, the renderer logs a **one-time** warning and skips that layer — it does not spam the Console every frame.

---

#### Ownership and separation rules (non-negotiable)

| Concern | Owner | Must not be touched by |
|---|---|---|
| Surface appearance (colour, texture, tint, opacity, UV) | `ArenaSurfaceSkinSet` | `PlayfieldFrustumProfile`, `ArenaColliderProvider`, `PlayerDebugArenaSurface` |
| Production rendering of the surface | `ArenaSurfaceRenderer` | Debug components |
| Frustum cone shape (Z heights) | `PlayfieldFrustumProfile` | `ArenaSurfaceSkinSet`, `ArenaSurfaceRenderer` (reads only) |
| Arena `MeshCollider`s for input | `ArenaColliderProvider` | `ArenaSurfaceSkinSet`, `ArenaSurfaceRenderer` |
| Debug fill mesh + debug colliders | `PlayerDebugArenaSurface` | Must not be the sole source of colliders in production |

`ArenaSurfaceRenderer` reads `PlayfieldFrustumProfile` for Z heights and reads `ChartRuntimeEvaluator` for geometry.  It does **not** write to either.

---

#### Transitional notes (future-proofing)

* **Additional layers:** The three-layer contract (base / detail / accent) is designed to be extensible.  A future `ArenaSurfaceLayer[]` array or a fourth procedural layer can be added to `ArenaSurfaceSkinSet` without changing the existing fields or breaking existing skin assets.
* **Dynamic / animated layers:** The `uvScrollSpeed` per-layer design accommodates simple animated energy effects in v0.  More complex per-frame procedural animation (e.g. shader-driven pulse or ripple) is deferred — do not add shader variants or animation curves to `ArenaSurfaceSkinSet` until specified.
* **Per-arena skin overrides:** The current contract applies one skin set to all arenas.  Per-arena skin override (different look per arena slot) is deferred.
* **Global fade (surfaceOpacityMultiplier):** The global multiplier is reserved for runtime fade effects (e.g. song intro/outro fade, scene transition).  Animating it via code is safe.  Keyframe-driven animation from the chart editor is deferred.

---

### **5.9) Runtime geometry evaluation pipeline (per-frame)**

**Parity rule:** `ChartRuntimeEvaluator`, `NoteApproachMath`, and the `EvaluatedArena` / `EvaluatedLane` / `EvaluatedCamera` structs are **shared, parity-critical systems**. Both the Player App and the Chart Editor Playfield Preview must consume these — not reimplement them. Any change to evaluation logic must be applied once in `Assets/_Project/Shared/` and takes effect in both apps immediately.

Arena, lane, and camera keyframe tracks are sampled **every frame** by `ChartRuntimeEvaluator`
(`Assets/_Project/Shared/Runtime/Evaluation/ChartRuntimeEvaluator.cs`).
`PlayerAppController.Update()` calls `_evaluator.Evaluate((int)chartTimeMs)` then
`SyncGeometryFromEvaluator()` to update `ArenaGeometries` / `LaneGeometries` dictionaries.

**Key design properties:**
* Zero per-frame allocations — `EvaluatedArena[]` and `EvaluatedLane[]` arrays are pre-allocated once in the constructor.
* Disabled arenas / lanes (`EnabledBool == false`) are **removed** from the geometry dicts so that
  JudgementEngine and all renderers skip them automatically (spec §5.6).
* Debug scaffolding (`PlayerDebugArenaSurface`, `PlayerDebugRenderer`) reads the same evaluated
  geometry — arena surface meshes and debug arc overlays animate in sync with production visuals.
* Angle tracks (`arcStartDeg`, `centerDeg`) use `FloatTrack.EvaluateAngleDeg` (shortest-path wrap).
* `ArcSweepDeg` uses plain `Evaluate` — it is a span, not a cyclic angle.
* Camera keyframes are only applied to the scene camera when the chart has authored position keyframes
  (guard against snapping the camera to default on charts without camera animation).

**Shared math utilities — `NoteApproachMath` (parity-critical)**
(`Assets/_Project/Shared/Runtime/Evaluation/NoteApproachMath.cs`)

These are **parity-critical** — the Player App and Chart Editor Playfield Preview must both use these helpers. Duplicating any of this logic in editor-side code creates divergence between the preview and the runtime. Any change to these formulas must be reflected in both apps simultaneously.

Static, allocation-free helpers used by all renderers, the evaluator, and the chart editor preview:

| Method | Purpose |
|---|---|
| `ApproachAlpha(timeToHitMs, noteLeadTimeMs)` | `1 − Clamp01(t / lead)` — position along approach path |
| `ApproachRadius(timeToHitMs, noteLeadTimeMs, spawnR, judgementR)` | `Lerp(spawnR, judgementR, alpha)` |
| `JudgementRadius(outerLocal, minDimLocal, insetNorm)` | `outerLocal − insetNorm × minDimLocal` |
| `SpawnRadius(innerLocal, judgementR, factor)` | `innerLocal + factor × (judgementR − innerLocal)` |
| `LaneChordWidthAtRadius(r, halfWidthDeg)` | `2 · r · sin(halfWidthDeg · Deg2Rad)` |
| `FrustumZAtRadius(r, innerLocal, outerLocal, hInner, hOuter)` | Linear Z ramp lifting XY onto cone surface |

### **5.10) Keyframe evaluation rules (deterministic, v0)**

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

### **5.11 Production feedback subsystems**

Gameplay feedback is divided into two independent production subsystems, separate from note renderers and debug overlays. Both systems must be usable without `PlayerDebugRenderer` or any debug component, and are skinnable via `GameplayFeedbackSkinSet` (§5.12).

**Architecture note (layered design, ArcCreate-inspired):**
This project follows a service/layered separation for feedback, inspired by how ArcCreate decouples input parsing, touch feedback, judgement, and judgement effects into independent systems. The layering principle:

| Layer | Responsibility | Does not |
|---|---|---|
| Input parsing | `PlayerAppController` projects touch to playfield XY; delivers events to `JudgementEngine` | Render anything |
| Touch feedback | Reads projected touch/lane state; renders active-lane indicators | Perform judgement |
| Judgement | `JudgementEngine` + `ScoreTracker` evaluate timing and scoring | Render feedback effects |
| Judgement effects | Listens to `OnJudgement` / `OnHoldTick` / `OnHoldResolved`; renders Perfect/Great/Miss visuals | Affect scoring |

This separation is not a copy of ArcCreate's implementation — only the layering principle is adopted.

#### **5.11.1 Input / touch feedback (planned)**

**Purpose:** Render lane touch indicators and active-lane highlights in response to current touch state.

**Design rules (locked for v0):**
* Separate `MonoBehaviour` from all note renderers and from `JudgementEngine`.
* Reads projected touch/lane membership state from `PlayerAppController` — no direct `UnityEngine.Input` polling.
* Does not perform any hit-testing or judgement evaluation.
* Reads lane geometry from `ChartRuntimeEvaluator` via `PlayerAppController.Evaluator` — same source as note renderers.
* Skinnable via `GameplayFeedbackSkinSet.touchFeedback` (§5.12).
* Uses `Graphics.DrawMesh` / `MaterialPropertyBlock` pattern — no child GameObjects per active touch.
* **Geometry is derived from the evaluated arena/lane surface** — touch indicator geometry uses the same radius terms used throughout this spec (`innerLocal`, `judgementRadiusLocal`, `visualOuterLocal` — see §5.8) and the same evaluated arena/lane dictionaries as `ArenaSurfaceRenderer` and `LaneGuideRenderer`. Coverage extents must be expressed using these defined boundaries, not ad-hoc approximations.
* **Must remain correct under camera motion** — the gameplay scene supports animated camera tracks (chart editor §8). Production touch feedback must be placed in world-space using the lane surface geometry so that it remains correct when the camera moves. A flat screen-facing approximation is not a valid production implementation.
* **Flat overlay approximation is prototyping/debug only** — a fixed-Z flat sector that looks acceptable from a single static camera angle is acceptable as a transitional placeholder during development only. It must not be treated as the intended final production geometry.

**Coverage modes (using spec-defined radii, §5.8):**

Lane touch feedback coverage is defined by the same radius terms used by production renderers:

| Mode | Inner edge | Outer edge |
|---|---|---|
| Near-judgement band | `max(innerLocal, judgementRadiusLocal − radialExtentLocal)` | `judgementRadiusLocal` |
| Full visible lane | `innerLocal` | `visualOuterLocal` |

The near-judgement band is the default mode: a thin indicator anchored at `judgementRadiusLocal` extending inward by `radialExtentLocal`. Full-lane mode covers the complete visible arena footprint from `innerLocal` to `visualOuterLocal`, matching `ArenaSurfaceRenderer`'s outer edge. `outerLocal` (the chart geometry boundary) must not be used as the outer edge in full-lane mode — use `visualOuterLocal` so the overlay aligns with the visual rim.

**v0 scope:** Implementation is planned; this section defines the contract and ownership rules. A placeholder with no feedback rendered is acceptable as a transitional state.

#### **5.11.2 Judgement feedback (planned)**

**Purpose:** Render Perfect / Great / Miss effect labels or particles at the judgement ring when notes are judged.

**Design rules (locked for v0):**
* Separate `MonoBehaviour` from all note renderers.
* Subscribes to `PlayerAppController.OnJudgement`, `PlayerAppController.OnHoldTick`, and `PlayerAppController.OnHoldResolved`.
* Does not modify score, combo, or any gameplay state.
* Effect position: centred at `judgementR` along the judged lane's center angle at the event time.
* Skinnable via `GameplayFeedbackSkinSet.judgementFeedback` (§5.12).
* Hold-specific feedback variants (e.g. a distinct per-tick flash) are reserved as a later step. v0 may treat hold tick feedback identically to single-note feedback.

**v0 scope:** Implementation is planned; this section defines the contract and ownership rules. A placeholder with no feedback rendered is acceptable as a transitional state.

---

### **5.12 GameplayFeedbackSkinSet (feedback skin asset)**

`GameplayFeedbackSkinSet` is a `ScriptableObject` that holds all authoring data for production gameplay feedback visuals. It is **separate from `NoteSkinSet`**:
* `NoteSkinSet` — note/hold/arrow body visuals (permanent identity of note types).
* `GameplayFeedbackSkinSet` — transient feedback effect visuals (touch indicators, judgement labels/particles).

**Separation rationale:** Feedback effects have fundamentally different authoring parameters from note bodies (timing, fade curves, label sizing) and must not pollute `NoteSkinSet`. Keeping them separate means either asset can be replaced independently.

Create via: **Assets → Create → RhythmicFlow → Gameplay Feedback Skin Set** *(planned)*.

**`GameplayFeedbackSkinSet` fields (v0 planned contract):**

*Touch feedback block:*

| Field | Type | Description |
|---|---|---|
| `touchFeedbackMaterial` | `Material` | Shader template for the active-lane touch indicator. Must support `_Color` and `_MainTex` via `MaterialPropertyBlock`. |
| `touchFeedbackTexture` | `Texture2D` | Texture for the active-lane highlight arc or quad. |
| `touchFeedbackColor` | `Color` | Base color tint applied via `MaterialPropertyBlock`. |
| `touchFeedbackRadialHalfThickness` | `float` | Radial half-thickness of the touch indicator in PlayfieldLocal units. |

**Touch feedback geometry note:** Coverage extents for touch feedback are defined by the spec-defined radius terms in §5.8 (`innerLocal`, `judgementRadiusLocal`, `visualOuterLocal`) and must match the coverage mode contract in §5.11.1. Skin parameters that control radial extent (e.g. `radialExtentLocal`, `fullLaneCoverage`) configure which coverage mode is used; they do not override the spec-defined boundaries themselves.

*Judgement feedback block:*

| Field | Type | Description |
|---|---|---|
| `judgementFeedbackMaterial` | `Material` | Shader template for Perfect/Great/Miss labels. Must support `_MainTex` and `_Color` via `MaterialPropertyBlock`. |
| `perfectTexture` | `Texture2D` | Label texture displayed on Perfect judgement. |
| `greatTexture` | `Texture2D` | Label texture displayed on Great judgement. |
| `missTexture` | `Texture2D` | Label texture displayed on Miss judgement. |
| `feedbackSizeLocal` | `float` | Size of the feedback label in PlayfieldLocal units. |
| `feedbackLifetimeMs` | `float` | Duration the feedback effect is visible, in milliseconds. |
| `feedbackFadeStartFraction` | `float` [0..1] | Fraction of `feedbackLifetimeMs` at which fade-out begins. |

*Hold-specific feedback (reserved for later):*
Hold tick feedback variants (e.g. a distinct flash per Perfect/Miss tick) are explicitly reserved. v0 may render hold tick feedback identically to single-note feedback. The hold-specific field block will be added when hold feedback migration is implemented.

**Data contract:** `GameplayFeedbackSkinSet` authoring fields must not be combined with `NoteSkinSet` and must not depend on any debug component.

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
* Each baked tick (`tickTimesMs[i]`) is a scored judgement event (spec §4.4):
  * if bound touch is down and inside lane at `tickTimeMs` → **Perfect** tick (combo++, +1000 pts)
  * else → **Miss** tick (combo reset, +0 pts, hold FAILS — no further ticks evaluated)
* **First missed tick fails the hold immediately.** The hold body stays visible (dim, `holdColorReleased`)
  until `endTimeMs` but no further tick judgements are emitted ("no spam" rule).
* Early release: sets `HoldBind = Finished`; one Miss tick is emitted for the first unprocessed
  tick so the combo breaks exactly once.
* Hold start miss (never bound): scores as one Miss at `StartTimeMs + GreatWindowMs` (spec §4.4).

### **7.5.1) Hold tick processing across frames (must-have)**

**Goal:** Avoid missing ticks on low FPS / hitches.

* Maintain `prevSongTimeMs`.
* Each update, process all hold ticks where:
  * `prevSongTimeMs < tickTimeMs <= currentSongTimeMs`
* Processing loop breaks immediately after the first Miss tick (hold fails; no spam).
* Same hitch-safe pattern applies to consuming time-based notes.

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
| `HitBandInnerCoverage01` | `0.35` | **Input only.** Fraction `[0..1]` of available inward depth accepted. `inwardDepth = judgementRadius − chartInner`; `hitInner = judgement − coverage × inwardDepth`. `0` = no inward tolerance; `1` = full depth to `chartInnerLocal`. Default 0.35 = 35 % of lane depth inward from judgement line. See §5.5.2. |
| `HitBandInnerInsetNorm` | *(deprecated)* | **Deprecated.** Replaced by `HitBandInnerCoverage01`. Kept in code for compatibility; no longer used for the inward bound. |
| `HitBandOuterInsetNorm` | `0.04` | **Input only.** Outward half-width of the hit band from `judgementRadiusLocal`: `hitBandOuter = judgement + HitBandOuterInsetNorm × minDim`. Default 0.04 = 4 % of `minDimLocal`. See §5.5.2. |
| `InputBandExpandInnerNorm` | `0.00` | **Input only.** Additional inner expansion subtracted from `hitBandInner` (additive fine-tune, see §5.5.2). Default 0 = no extra inner expansion. |
| `InputBandExpandOuterNorm` | `0.03` | **Input only.** Additional outer expansion added on top of `hitBandOuter` (additive fine-tune, see §5.5.2). Default 0.03 = 3 % extra outward. Clamped to `visualOuterLocal`. |
| `JudgementInsetNorm` | `0.003` | **Visual/skin only.** Insets the judgement ring inside `outerLocal` by `JudgementInsetNorm × minDimLocal`. Notes land and the judgement arc is drawn at this radius. Does not change hit-testing, timing windows, or chart geometry. See §5.8. |
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
  * MaxCombo
  * Perfect/Great/Miss counts (includes hold ticks in Perfect and Miss totals)
  * hold tick sub-counts: `HoldTickPerfectCount` / `HoldTickMissCount`
  * Accuracy % (weighted: Perfect×1000 + Great×700 / TotalJudged×1000)
  * optional early/late breakdown (future)

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