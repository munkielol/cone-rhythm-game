# CLAUDE_TASKS.md — Overnight v0 implementation (scripts-only, strict spec compliance)
If you stop early for any reason, print:
- the last completed task number
- the next task to run
- `git log -10 --oneline`
Then stop (no push).

## Enabled safe-default prompt improvements (1,2,3,4,6,7,11,12,13,16,19)
- (1) Hard denylist for Unity YAML/binaries + auto-revert
- (2) No push + enforced work branch (`nightly/claude-v0`)
- (3) Commit gate: forbidden-files check before every commit
- (4) Stop when unsure: no spec invention (TODO + continue)
- (6) Spec-trace comments for schema/rules (`// Spec: Player §X.Y` / `// Spec: Editor §X.Y`)
- (7) Golden JSON fixtures + validator runner
- (11) Dependency-correct task ordering
- (12) One commit per task
- (13) No refactors / no renames / no churn
- (16) PowerShell-first command style
- (19) Final report includes git log + status + TODO list


Read first:
- Follow `CLAUDE.md`.
- Source of truth:
  - `Docs/Specs/player_app_v0_specs.md`
  - `Docs/Specs/chart_editor_v0_specs.md`

HARD RULES (non-negotiable):
- Scripts only. DO NOT create/edit: `.unity`, `.prefab`, `.asset`, `.mat`, `.anim`, `.controller`, `.fbx`, `.png`, `.wav`, `.ogg`, `.shader`, `.shadergraph` or any YAML-based Unity files.
- DO NOT push to origin.
- DO NOT add external packages/libs.
- **No refactors/renames:** do not rename/move folders/classes/files unless explicitly required by a task.
- Keep code under correct asmdef folders:
  - `Assets/_Project/Shared/Runtime/...`
  - `Assets/_Project/Player/Runtime/...`
  - `Assets/_Project/ChartEditorApp/Runtime/...`
- Commit after each task with the exact commit message.
- If something is underspecified, do NOT invent new schema fields. Add TODO and leave a short note in commit body.

EXECUTION ENVIRONMENT:
- Windows. Use PowerShell for shell commands if needed.
- If the tool is named "Bash" in this environment, still run PowerShell like:
  - `powershell -NoProfile -Command "<COMMAND>"`

----------------------------------------------------------------------
GUARDRAILS — MUST RUN BEFORE EVERY COMMIT (and at end)
----------------------------------------------------------------------

Guardrail G0 — Enforce branch:
- Before committing, confirm you are on `nightly/claude-v0` (`git branch --show-current`). If not, STOP and switch.


Guardrail G1 — Forbidden file changes:
- Before committing, run `git status --porcelain` and ensure there are NO changes matching:
  - `*.unity`, `*.prefab`, `*.asset`, `*.mat`, `*.anim`, `*.controller`, `*.fbx`, `*.png`, `*.jpg`, `*.wav`, `*.ogg`, `*.shader`, `*.shadergraph`
- If any exist: **immediately auto-revert** them (e.g., `git checkout -- <file>`) and continue scripts-only. Do not commit forbidden files.

Guardrail G2 — No pushing:
- Do not run `git push` at any time.

Guardrail G3 — Small, reviewable commits:
- Each task is a separate commit.
- Do not bundle unrelated changes.

Guardrail G4 — Spec alignment:
- If you add or change schema fields, you must cite the exact spec section in a code comment near the field definition.

----------------------------------------------------------------------
Task 0 — Preflight (no code changes, no commit)
----------------------------------------------------------------------

- Confirm git working tree is clean.
- Confirm branch is `nightly/claude-v0` (if not, create it).
  - If branch does not exist: `git checkout -b nightly/claude-v0`
  - Else: `git checkout nightly/claude-v0`
- Print:
  - current branch
  - last 3 commits
  - the spec file paths you are following

----------------------------------------------------------------------
Task 1 — Audit & harden existing Shared ChartJsonV1 implementation (NO NEW SCHEMA)
Commit: `Shared: audit ChartJsonV1 against specs`

You MUST assume Task 1 (Shared model/reader/validator) already exists.
Do NOT re-implement from scratch unless absolutely necessary.

1. Locate existing code under:
- `Assets/_Project/Shared/Runtime/`

2. Compare against specs and ensure:
A) Data model completeness (v0):
- Root chart fields per specs (version, song metadata incl audioOffsetMs)
- arenas/lanes/notes structures present
- Tracks: Keyframe(timeMs,value,easing), Track(list of keyframes)
- Notes:
  - Tap: timeMs
  - Flick: timeMs (+ direction if spec requires)
  - Catch: timeMs
  - Hold: startTimeMs, endTimeMs, tickTimesMs[] authoritative
- IDs:
  - arenaId, laneId, noteId fields exist where specified
  - notes reference laneId, lanes reference arenaId

B) JSON parsing robustness:
- Provide `TryReadFromFile(path, out chart, out error)`
- Provide `TryReadFromText(json, out chart, out error)`
- No external JSON libs.
- If Unity JsonUtility limitations exist, ensure the model is JsonUtility-friendly (avoid Dictionary).
- Error strings must be actionable.

C) Validation strictness (export-blocking rules):
- version supported
- required arrays non-null
- IDs non-empty and uniqueness rules as spec
- references valid (notes->laneId, lanes->arenaId)
- keyframes sorted by timeMs
- duplicate timeMs => ERROR
- enabled track:
  - values must be 0 or 1
  - easing must be "hold" only
  - violation => ERROR
- hold:
  - start < end
  - tickTimes sorted
  - tickTimes unique
  - tickTimes within [start,end]
  - violations => ERROR
- sanity ranges:
  - opacity 0..1 (error if outside)
  - arcSweep within (0..360] (error if outside)
  - widthDeg > 0 (error if <=0)
  - other geometry sanity per spec (warn vs error must match spec intent)

D) Preserve file order:
- Ensure note list ordering can be used as stable `noteIndex` tie-break.
- If needed, ensure validator assigns/derives noteIndex without mutating authoring data (or document clearly).

3. Make minimal amendments required for strict spec compliance.
4. Add/ensure `ChartDebugSummary.BuildSummary(chart)` exists and is accurate.

Before commit: run Guardrails G1–G4.

----------------------------------------------------------------------
Task 2 — Add Shared “Spec Fixtures” (JSON samples + validator test harness)
Commit: `Shared: add spec fixtures and validator harness`

Goal: Make overnight work verifiable without Unity scene edits.

1) Create:
- `Docs/SpecFixtures/`
  - `ChartJsonV1_Minimal_TapOnly.json`
  - `ChartJsonV1_WithHoldTicks.json`
  - `ChartJsonV1_Invalid_DuplicateKeyframe.json`
  - `ChartJsonV1_Invalid_EnabledNotHold.json`

2) Implement Shared test harness (scripts only):
- `Assets/_Project/Shared/Runtime/Validation/ChartValidatorRunner.cs`
  - static method `RunFixtureValidation(string fixturePath)` returns result string
  - not a Unity scene; just callable from future tooling

3) Ensure fixtures match schema exactly (no invented fields).

Before commit: Guardrails G1–G4.

----------------------------------------------------------------------
Task 3 — Shared: .rpk (zip) reader (read-only)
Commit: `Shared: add .rpk reader`

Implement:
- `RpkReader` under `Assets/_Project/Shared/Runtime/IO/`
  - open `.rpk` from disk (zip)
  - read text file contents inside (songinfo, chart json)
  - enumerate entries robustly
  - return actionable errors

No scene changes, no UI.

Before commit: Guardrails G1–G4.

----------------------------------------------------------------------
Task 4 — Player: Conductor + offsets + simple settings store
Commit: `Player: add Conductor and timing offsets`

Implement under `Assets/_Project/Player/Runtime/`:
- `Conductor` (DSP-time based)
  - start/stop
  - `SongDspTimeMs`
  - `EffectiveChartTimeMs = SongDspTimeMs + song.audioOffsetMs + UserOffsetMs` (exact spec)
- `PlayerSettingsStore`
  - `UserOffsetMs` persisted (PlayerPrefs OK v0)
- No UI, no scene edits.

Before commit: Guardrails.

----------------------------------------------------------------------
Task 5 — Player: Pack scanning + catalog (no UI)
Commit: `Player: add pack scanning and catalog`

Implement:
- `PackScanner` scans packs directory for `.rpk`
- loads songinfo + chart via Shared
- excludes invalid packs (log reason)
- outputs `PackCatalog` in-memory list for future Song Select

No UI, no scene edits.

Before commit: Guardrails.

----------------------------------------------------------------------
Task 6 — Player: Playfield mapping + aspect-safe hit-testing + angle utilities
Commit: `Player: add playfield mapping and hit-test math`

Implement (code only):
- `PlayfieldTransform` math:
  - NormalizedToLocal / LocalToNormalized
  - minDimLocal
- `AngleUtil`:
  - normalize360
  - shortestSignedAngleDeltaDeg
  - wrap-safe arc span contains
- `ArenaHitTester`:
  - MUST use PlayfieldLocal aspect-safe rules from specs
- Include frustum visual mapping helper (math only, no meshes)

Before commit: Guardrails.

----------------------------------------------------------------------
Task 7 — Player: Runtime note model + scheduler (no rendering)
Commit: `Player: add note runtime model and scheduler`

Implement:
- runtime note objects/structs for Tap/Flick/Catch/Hold
- preserve authoring order noteIndex
- `NoteScheduler`:
  - active note queries by time window
  - hold tick evaluation safe across frame gaps

Before commit: Guardrails.

----------------------------------------------------------------------
Task 8 — Player: Judgement engine + arbitration (no rendering)
Commit: `Player: add judgement system and arbitration`

Implement:
- Judgement windows + result types
- Candidate selection + tie-break order exactly per spec:
  1) smallest abs timing error
  2) smallest angular distance to lane centerline
  3) higher lane priority
  4) stable fallback lower noteIndex
- hold binding logic
- log judgements for headless verification

Before commit: Guardrails.

----------------------------------------------------------------------
Task 9 — ChartEditorApp: Project model + undo stack (no UI, no scenes)
Commit: `ChartEditorApp: add project model and undo stack`

Implement:
- `EditorProject` model (rproj json load/save) per spec
- undo/redo command pattern for core operations (data-only)
- integrates Shared validation

Before commit: Guardrails.

----------------------------------------------------------------------
Task 10 — ChartEditorApp: Exporter to .rpk + validation gates
Commit: `ChartEditorApp: add .rpk export pipeline`

Implement:
- `RpkExporter` that creates `.rpk` zip
- blocks export on validation errors
- ogg-only audio rules per spec
- no UI and no scene changes

Before commit: Guardrails.

----------------------------------------------------------------------
Task 11 — Final compliance report (no code changes, no commit)
----------------------------------------------------------------------

Print a report with:
- What tasks completed + commit hashes
- **TODO list** (all TODOs discovered/added, grouped by area)
- Confirmation that Guardrails G0–G4 passed (especially no forbidden file changes)
- Output:
  - `git status`
  - `git log --oneline --decorate -20`
DO NOT push.