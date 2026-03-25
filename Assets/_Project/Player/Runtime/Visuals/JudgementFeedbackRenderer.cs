// JudgementFeedbackRenderer.cs
// Production judgement-feedback renderer (spec §5.11.2).
//
// ── What this renderer does ───────────────────────────────────────────────────
//
//   Job 1 (skeleton):
//     • Subscribes to PlayerAppController.OnJudgement, .OnHoldTick, .OnHoldResolved.
//     • Manages a pre-allocated pool of 32 ActiveFeedback slots.
//     • On each event: looks up the correct JudgementFeedbackEntry from the skin,
//       claims a free pool slot, and records spawn data.
//     • Each LateUpdate: advances ages and expires elapsed slots.
//
//   Job 2 (spawn position, billboard, animation, draw):
//     • Computes the world-space spawn position at the lane's judgement ring point:
//         – XY: arena center + cos/sin(lane.CenterDeg) × judgementRadius
//         – Z:  FrustumZAtRadius(judgementR) + entry.surfaceOffsetLocal
//       Stored in SpawnPositionLocal (pfRoot local space) at spawn time.
//     • Each LateUpdate draws each active slot as a world-space camera-facing quad:
//         – Drift:     localPos.z += driftSpeedLocal × age (above-surface float)
//         – Billboard: Quaternion.LookRotation(camPos − worldPos, camUp)
//         – Pop/scale: starts at 60% size, grows to 100% over the first 15% of lifetime.
//         – Fade-out:  full opacity until (lifetime − fadeOutDuration), then linear fade.
//     • Draws via Graphics.DrawMesh + one shared unit-quad mesh + one MaterialPropertyBlock.
//
//   Job 3 (this step — policy correctness and result-family correctness):
//     • Implements the runtime meaning of JudgementStackPolicy:
//         – Stack:   effects coexist freely; no change to existing behaviour.
//         – Replace: before spawning, deactivates any prior active effect that belongs
//                    to the same lane (matched by LaneId).  This ensures at most one
//                    effect per lane is visible at a time.
//                    Replace only fires when we are confirmed to spawn something — it
//                    does not clear prior effects for misses with enabled = false.
//     • Implements Perfect+ fallback:
//         – When useSeparatePerfectPlusVisual is true but the perfectPlus entry is
//           unusable (disabled or null material), the renderer falls back silently to
//           the standard perfect entry, so Perfect+ hits are never visually suppressed
//           just because the extra entry is unconfigured.
//         – When useSeparatePerfectPlusVisual is false, Perfect+ hits use perfect
//           directly (unchanged from Job 2 — already handled by GetJudgementEntryForTier).
//     • Miss coverage is complete:
//         – Direct tap/catch/flick misses → OnJudgement → HandleJudgement.
//         – Sweep-missed non-hold notes   → OnJudgement → HandleJudgement.
//         – Sweep-missed holds (Unbound/Finished) → OnHoldResolved → HandleHoldResolved.
//         – Hold-tick misses (failed tick / early release) → OnHoldTick → HandleHoldTick.
//       All four paths flow through SpawnEffect and respect the same entry.enabled flag,
//       so the silent-miss design (enabled = false) is applied consistently everywhere.
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT affect input, hit-testing, judgement, or scoring.
//   • Does NOT add hold-specific visual families (deferred, spec §5.11.2).
//   • Does NOT depend on PlayerDebugRenderer or any debug component.
//
// ── Spawn position formula ────────────────────────────────────────────────────
//
//   In pfRoot local space:
//     x = arenaCenter.x + cos(laneGeo.CenterDeg) × judgementR
//     y = arenaCenter.y + sin(laneGeo.CenterDeg) × judgementR
//     z = FrustumZAtRadius(judgementR, innerLocal, outerLocal, hInner, hOuter)
//         + entry.surfaceOffsetLocal
//
//   Same geometry source as LaneTouchFeedbackRenderer and JudgementRingRenderer.
//
// ── Billboard facing ──────────────────────────────────────────────────────────
//
//   Per frame in LateUpdate:
//     1. currentLocalPos = SpawnPositionLocal + (0, 0, driftSpeedLocal × age)
//     2. worldPos        = pfRoot.TransformPoint(currentLocalPos)
//     3. billboardRot    = Quaternion.LookRotation(camPos − worldPos, camUp)
//     4. TRS             = Matrix4x4.TRS(worldPos, billboardRot, Vector3.one × worldScale)
//
// ── Replace policy ────────────────────────────────────────────────────────────
//
//   Matching key: LaneId (string).
//   Each unique lane has one judgement-ring position, so LaneId alone identifies the
//   "position family" for this renderer.  No tier grouping is applied — any active
//   effect on the same lane is replaced, regardless of whether it was Perfect or Miss.
//
//   Replacement runs AFTER all entry/geometry guards succeed, so it never clears
//   a prior effect for a miss that will not itself spawn anything.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   • One shared unit-quad Mesh, pre-built in Awake, destroyed in OnDestroy.
//   • One shared MaterialPropertyBlock, cleared before each draw call.
//   • Each active slot calls Graphics.DrawMesh once per LateUpdate.
//
// ── Wiring ────────────────────────────────────────────────────────────────────
//
//   1. Add this component to any GO in the Player scene.
//   2. Assign playerAppController, frustumProfile, and skinSet in the Inspector.
//   3. Assign a material (e.g. Unlit/Transparent) to each enabled judgement entry.
//   4. Optionally assign textures for label/sprite looks.
//
// ── Spec reference ────────────────────────────────────────────────────────────
//
//   Spec §5.11.2 (judgement feedback design rules) / §5.12 (skin contract).

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production judgement-feedback renderer (spec §5.11.2).
    /// Subscribes to <see cref="PlayerAppController"/> judgement events, maintains a
    /// pre-allocated effect pool, and draws each active effect as a camera-facing
    /// billboard quad with pop/drift/fade animation.
    ///
    /// <para>Visual-only — does not affect input, judgement, scoring, or note rendering.</para>
    ///
    /// <para>Attach to any GO in the Player scene.  Assign
    /// <see cref="playerAppController"/>, <see cref="frustumProfile"/>, and
    /// <see cref="skinSet"/> in the Inspector.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/JudgementFeedbackRenderer")]
    public class JudgementFeedbackRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController to subscribe to for judgement events " +
                 "(OnJudgement, OnHoldTick, OnHoldResolved).")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Shared frustum profile.  Used to place effects at the correct Z height on the " +
                 "arena cone surface.\n\nWhen null or UseFrustumProfile is false, the effect base " +
                 "Z defaults to 0.001 (flat) plus entry.surfaceOffsetLocal.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Tooltip("Gameplay feedback skin.  Source of truth for all judgement effect appearance.\n\n" +
                 "Entries: perfectPlus / perfect / great / miss — each with material, tint, size,\n" +
                 "lifetime, fade, drift, and enabled flag.\n\n" +
                 "stackPolicy: Stack = effects accumulate per lane;  " +
                 "Replace = a new effect deactivates any prior active effect on the same lane.")]
        [SerializeField] private GameplayFeedbackSkinSet skinSet;

        // -------------------------------------------------------------------
        // Animation constants
        // -------------------------------------------------------------------

        // Maximum number of simultaneously active judgement effects.
        private const int PoolSize = 32;

        // Pop scale animation constants.
        // These are intentionally not skin parameters to keep the skin contract minimal.
        //   First PopFraction of lifetime: scale grows from PopStartScale to 1.0.
        //   Remaining lifetime: scale holds at 1.0 while opacity fades.
        private const float PopFraction   = 0.15f;
        private const float PopStartScale = 0.60f;

        // -------------------------------------------------------------------
        // Pool element — one active judgement effect instance
        //
        // Struct (value type) — pool array allocated once in Awake, never resized.
        // -------------------------------------------------------------------
        private struct ActiveFeedback
        {
            /// <summary>True when this slot holds a live effect.</summary>
            public bool IsActive;

            /// <summary>Seconds elapsed since spawn.  Incremented by Time.deltaTime each LateUpdate.</summary>
            public float Age;

            /// <summary>
            /// Total lifetime in seconds (snapshot of Config.lifetime at spawn time so
            /// skin hot-reloads don't change mid-flight behaviour).
            /// </summary>
            public float Lifetime;

            /// <summary>LaneId of the note that triggered this effect (string ref — no allocation).</summary>
            public string LaneId;

            /// <summary>
            /// Snapshot of the skin entry at spawn time.
            /// Animation values (size, fade, drift, tint) come from this copy, not the live asset.
            /// </summary>
            public JudgementFeedbackEntry Config;

            /// <summary>True when triggered by a Perfect+ hit (spec §4.3, display-only).</summary>
            public bool IsPerfectPlus;

            /// <summary>
            /// Effect origin in pfRoot local space at Age = 0.
            /// The drift component (driftSpeedLocal × age) is added per-frame at draw time.
            /// </summary>
            public Vector3 SpawnPositionLocal;
        }

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Pre-allocated pool.  Allocated once in Awake, never resized.
        private ActiveFeedback[] _pool;

        // Single shared unit-quad mesh (built in Awake, destroyed in OnDestroy).
        private Mesh _quadMesh;

        // Single reused MaterialPropertyBlock — cleared before each draw call to
        // prevent texture/color bleed between slots with different entries.
        private MaterialPropertyBlock _propBlock;

        // Cached Camera.main — re-fetched when null (handles scene reload).
        private Camera _mainCamera;

        // Whether we have subscribed to the controller events.
        private bool _subscribed;

        // Once-fired warning flags.
        private bool _hasWarnedMissingSkinSet;
        private bool _hasWarnedGeometryNotFound;

        // Per-tier missing-material mask (one bit per logical tier).
        // Bit layout:
        //   bit 0 = Miss         (JudgementTier.Miss    = 0)
        //   bit 1 = Great        (JudgementTier.Great   = 1)
        //   bit 2 = Perfect      (JudgementTier.Perfect = 2)
        //   bit 3 = Perfect+     (isPerfectPlus && tier == Perfect, after fallback is applied)
        private int _missingMaterialWarnedMask;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _pool      = new ActiveFeedback[PoolSize];
            _propBlock = new MaterialPropertyBlock();
            _quadMesh  = BuildUnitQuad();
        }

        private void OnEnable()  { TrySubscribe(); }
        private void OnDisable() { TryUnsubscribe(); }

        private void OnDestroy()
        {
            TryUnsubscribe();
            if (_quadMesh != null) { Destroy(_quadMesh); _quadMesh = null; }
        }

        // -------------------------------------------------------------------
        // Per-frame update
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            // Lazy subscription: keep trying until playerAppController is assigned.
            TrySubscribe();

            // Re-fetch camera if lost (e.g. scene reload).
            if (_mainCamera == null) { _mainCamera = Camera.main; }

            float dt = Time.deltaTime;

            // ── Age advancement and expiry ─────────────────────────────────────────
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { continue; }

                _pool[i].Age += dt;

                if (_pool[i].Age >= _pool[i].Lifetime)
                {
                    _pool[i].IsActive = false;
                }
            }

            // ── Guard: all references required for drawing ─────────────────────────
            if (_mainCamera == null)         { return; }
            if (_quadMesh   == null)         { return; } // should not happen after Awake
            if (playerAppController == null) { return; }

            Transform pfRoot = playerAppController.playfieldRoot;
            if (pfRoot == null) { return; }

            // pfRoot.lossyScale.x converts PlayfieldLocal units to world units (uniform scale assumed).
            float   rootScale = pfRoot.lossyScale.x;
            Vector3 camPos    = _mainCamera.transform.position;
            Vector3 camUp     = _mainCamera.transform.up;

            // ── Draw active slots ─────────────────────────────────────────────────
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { continue; }

                float                  age      = _pool[i].Age;
                float                  lifetime = _pool[i].Lifetime;
                JudgementFeedbackEntry cfg      = _pool[i].Config; // struct copy, no alloc

                // Material guard — skipped at spawn when null; defend against hot-reload.
                if (cfg.material == null) { continue; }

                // ── Current position: spawn base + upward Z drift ──────────────────
                // Drift is in pfRoot local +Z (above arena cone surface).
                Vector3 localPos = _pool[i].SpawnPositionLocal;
                localPos.z += cfg.driftSpeedLocal * age;
                Vector3 worldPos = pfRoot.TransformPoint(localPos);

                // ── Camera-facing billboard rotation ───────────────────────────────
                Vector3 toCam = camPos - worldPos;
                if (toCam.sqrMagnitude < 1e-6f) { continue; } // camera inside effect — degenerate
                Quaternion billboardRot = Quaternion.LookRotation(toCam, camUp);

                // ── Pop scale ──────────────────────────────────────────────────────
                float popDuration = lifetime * PopFraction;
                float popScale = (popDuration > 0f && age < popDuration)
                    ? Mathf.Lerp(PopStartScale, 1.0f, age / popDuration)
                    : 1.0f;

                // ── Fade-out alpha ─────────────────────────────────────────────────
                float fadeStartTime = lifetime - cfg.fadeOutDuration;
                float fadeAlpha = (cfg.fadeOutDuration > 0f && age >= fadeStartTime)
                    ? 1f - (age - fadeStartTime) / cfg.fadeOutDuration
                    : 1f;
                fadeAlpha = Mathf.Clamp01(fadeAlpha);

                // ── World-space scale ──────────────────────────────────────────────
                // cfg.sizeLocal = half-extent.  Unit quad has half-extent 0.5, so:
                //   TRS scale = 2 × sizeLocal × rootScale × popScale.
                float worldScale = 2f * cfg.sizeLocal * rootScale * popScale;
                if (worldScale <= 0f) { continue; }

                // ── MaterialPropertyBlock ──────────────────────────────────────────
                _propBlock.Clear();
                Color drawColor = cfg.tint;
                drawColor.a = cfg.tint.a * cfg.opacity * fadeAlpha;
                _propBlock.SetColor("_Color", drawColor);
                if (cfg.texture != null) { _propBlock.SetTexture("_MainTex", cfg.texture); }

                // ── Draw ───────────────────────────────────────────────────────────
                Matrix4x4 trs = Matrix4x4.TRS(worldPos, billboardRot, Vector3.one * worldScale);
                Graphics.DrawMesh(_quadMesh, trs, cfg.material,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Event subscription helpers
        // -------------------------------------------------------------------

        private void TrySubscribe()
        {
            if (_subscribed || playerAppController == null) { return; }

            playerAppController.OnJudgement    += HandleJudgement;
            playerAppController.OnHoldTick     += HandleHoldTick;
            playerAppController.OnHoldResolved += HandleHoldResolved;
            _subscribed = true;
        }

        private void TryUnsubscribe()
        {
            if (!_subscribed || playerAppController == null) { return; }

            playerAppController.OnJudgement    -= HandleJudgement;
            playerAppController.OnHoldTick     -= HandleHoldTick;
            playerAppController.OnHoldResolved -= HandleHoldResolved;
            _subscribed = false;
        }

        // -------------------------------------------------------------------
        // Judgement event handlers
        // -------------------------------------------------------------------

        // All four miss paths — direct fail, sweep-miss, hold-tick miss,
        // hold-resolve miss — flow through these handlers into SpawnEffect,
        // where the same entry.enabled check is applied uniformly.

        // Tap / catch / flick hits and sweep-misses of non-hold notes.
        private void HandleJudgement(JudgementRecord r)    { SpawnEffect(r); }

        // Hold-tick judgements (Perfect or Miss per tick, spec §7.5.1).
        // Hold-specific visual variants are deferred (spec §5.11.2).
        private void HandleHoldTick(JudgementRecord r)     { SpawnEffect(r); }

        // Hold-resolved judgements (Perfect = held through; Miss = unbound/expired).
        // Hold-specific visual variants are deferred.
        private void HandleHoldResolved(JudgementRecord r) { SpawnEffect(r); }

        // -------------------------------------------------------------------
        // Pool spawn
        // -------------------------------------------------------------------

        // Resolves the correct skin entry, applies the Perfect+ fallback if needed,
        // validates guards, applies the Replace policy, and activates a pool slot.
        // Zero allocation in the hot path after initial setup.
        private void SpawnEffect(JudgementRecord r)
        {
            // ── Guard: skin required ───────────────────────────────────────────────
            if (skinSet == null)
            {
                if (!_hasWarnedMissingSkinSet)
                {
                    _hasWarnedMissingSkinSet = true;
                    Debug.LogWarning("[JudgementFeedbackRenderer] skinSet is not assigned. " +
                                     "Judgement feedback will not render.  " +
                                     "Assign a GameplayFeedbackSkinSet in the Inspector.");
                }
                return;
            }

            // ── Resolve the skin entry with Perfect+ fallback ──────────────────────
            //
            // Step 1: ask the skin for the entry based on tier + isPerfectPlus flag.
            //   GetJudgementEntryForTier already handles:
            //     • useSeparatePerfectPlusVisual = false  → returns perfect directly.
            //     • useSeparatePerfectPlusVisual = true   → returns perfectPlus.
            //
            // Step 2 (new in Job 3): if useSeparatePerfectPlusVisual is true but the
            //   perfectPlus entry is not usable (disabled, or no material assigned),
            //   fall back silently to the standard perfect entry.  This prevents
            //   Perfect+ hits from going dark just because the extra entry is
            //   unconfigured.  No warning is emitted for the fallback itself —
            //   it is expected when the designer has not yet set up perfectPlus.
            bool wantsPerfectPlus = r.Tier       == JudgementTier.Perfect
                                 && r.IsPerfectPlus
                                 && skinSet.useSeparatePerfectPlusVisual;

            JudgementFeedbackEntry entry      = skinSet.GetJudgementEntryForTier(r.Tier, r.IsPerfectPlus);
            bool                   didFallback = false;

            if (wantsPerfectPlus && (!entry.enabled || entry.material == null))
            {
                // perfectPlus is unusable — use the standard perfect entry instead.
                entry      = skinSet.perfect;
                didFallback = true;
            }

            // ── Enabled check ──────────────────────────────────────────────────────
            // A disabled entry is a valid no-op (e.g. silent miss by default).
            // Do NOT log here — disabled is intentional, not a misconfiguration.
            if (!entry.enabled) { return; }

            // ── Material guard ─────────────────────────────────────────────────────
            // After a Perfect+ fallback, we are using the perfect material slot;
            // report any missing-material warning for "Perfect", not "Perfect+".
            if (entry.material == null)
            {
                WarnMissingMaterial(r.Tier, r.IsPerfectPlus && !didFallback);
                return;
            }

            // ── Compute spawn position ─────────────────────────────────────────────
            if (!TryComputeSpawnPosition(r.Note.LaneId, entry.surfaceOffsetLocal,
                                         out Vector3 spawnLocalPos))
            {
                if (!_hasWarnedGeometryNotFound)
                {
                    _hasWarnedGeometryNotFound = true;
                    Debug.LogWarning("[JudgementFeedbackRenderer] Lane geometry not found for " +
                                     $"laneId='{r.Note.LaneId}'.  Effect skipped.  This may indicate " +
                                     "that geometry dictionaries are not yet populated or the lane " +
                                     "is disabled at the moment of judgement.");
                }
                return;
            }

            // ── Apply Replace policy ────────────────────────────────────────────────
            //
            // This block runs AFTER all entry/geometry guards succeed, so it only
            // fires when we are confirmed to spawn a new visible effect.
            //
            // Stack  (default): multiple effects coexist — no action needed.
            // Replace:          deactivate every prior active effect on the same lane
            //                   before claiming a new slot.  LaneId is the matching key
            //                   because each lane has one judgement-ring position, making
            //                   lane identity the natural "position family" for this renderer.
            if (skinSet.stackPolicy == JudgementStackPolicy.Replace)
            {
                string laneId = r.Note.LaneId;
                for (int i = 0; i < PoolSize; i++)
                {
                    // _pool[i].LaneId equality: uses reference-equal fast path first
                    // (likely matches since LaneId strings come from the same chart source),
                    // then falls back to value comparison.  O(32) — negligible.
                    if (_pool[i].IsActive && _pool[i].LaneId == laneId)
                    {
                        _pool[i].IsActive = false;
                    }
                }
            }

            // ── Claim a pool slot ──────────────────────────────────────────────────
            int slot = FindFreeSlot();

            // ── Write spawn data ───────────────────────────────────────────────────
            // All field assignments are value or reference copies — no allocation.
            _pool[slot].IsActive           = true;
            _pool[slot].Age                = 0f;
            _pool[slot].Lifetime           = entry.lifetime;
            _pool[slot].LaneId             = r.Note.LaneId;     // string ref copy
            _pool[slot].Config             = entry;             // struct copy
            _pool[slot].IsPerfectPlus      = r.IsPerfectPlus;
            _pool[slot].SpawnPositionLocal = spawnLocalPos;
        }

        // -------------------------------------------------------------------
        // Spawn position helper
        // -------------------------------------------------------------------

        // Computes the spawn position in pfRoot local space at the lane's
        // judgement ring point.  Returns false if any lookup fails.
        private bool TryComputeSpawnPosition(string laneId, float surfaceOffsetLocal,
                                             out Vector3 spawnLocalPos)
        {
            spawnLocalPos = Vector3.zero;

            var laneGeos    = playerAppController.LaneGeometries;
            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneToArena = playerAppController.LaneToArena;
            PlayfieldTransform pfT = playerAppController.PlayfieldTf;

            if (laneGeos == null || arenaGeos == null || laneToArena == null || pfT == null)
            {
                return false;
            }

            if (!laneGeos.TryGetValue(laneId,    out LaneGeometry laneGeo))    { return false; }
            if (!laneToArena.TryGetValue(laneId,  out string arenaId))          { return false; }
            if (!arenaGeos.TryGetValue(arenaId,   out ArenaGeometry arenaGeo))  { return false; }

            // Arena radii in PlayfieldLocal units.
            float outerLocal = pfT.NormRadiusToLocal(arenaGeo.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(arenaGeo.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Judgement ring radius — same formula as LaneTouchFeedbackRenderer (spec §5.8).
            float judgementR = NoteApproachMath.JudgementRadius(
                outerLocal, pfT.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);

            // Frustum Z — lifts onto the cone surface.  Falls back to near-zero flat Z.
            bool  useProfile = frustumProfile != null && frustumProfile.UseFrustumProfile;
            float hInner     = useProfile ? frustumProfile.FrustumHeightInner : 0.001f;
            float hOuter     = useProfile ? frustumProfile.FrustumHeightOuter : 0.001f;
            float surfaceZ   = NoteApproachMath.FrustumZAtRadius(
                judgementR, innerLocal, outerLocal, hInner, hOuter);

            // Arena center in PlayfieldLocal space.
            Vector2 center = pfT.NormalizedToLocal(
                new Vector2(arenaGeo.CenterXNorm, arenaGeo.CenterYNorm));

            // XY: on the judgement ring at the lane's center angle.
            float angleRad = laneGeo.CenterDeg * Mathf.Deg2Rad;
            float x        = center.x + Mathf.Cos(angleRad) * judgementR;
            float y        = center.y + Mathf.Sin(angleRad) * judgementR;
            float z        = surfaceZ + surfaceOffsetLocal;

            spawnLocalPos = new Vector3(x, y, z);
            return true;
        }

        // -------------------------------------------------------------------
        // Pool slot selection
        // -------------------------------------------------------------------

        // Returns the index of a free (inactive) slot, or the slot closest to
        // expiry (highest age/lifetime ratio) when the pool is full.  O(32).
        private int FindFreeSlot()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { return i; }
            }

            // Pool full — evict the slot closest to expiry (least visual impact).
            int   evictSlot = 0;
            float maxRatio  = -1f;

            for (int i = 0; i < PoolSize; i++)
            {
                float ratio = _pool[i].Lifetime > 0f
                    ? _pool[i].Age / _pool[i].Lifetime
                    : 1f;

                if (ratio > maxRatio) { maxRatio = ratio; evictSlot = i; }
            }

            return evictSlot;
        }

        // -------------------------------------------------------------------
        // Missing-material warning helper
        // -------------------------------------------------------------------

        // Logs a once-only warning per logical tier when a material is null at spawn time.
        // Bit mask prevents repeat warnings across multiple judgement events.
        //
        // isPerfectPlus should be false when this is called after a Perfect+ fallback —
        // in that case we are using the perfect entry and should report "Perfect", not "Perfect+".
        private void WarnMissingMaterial(JudgementTier tier, bool isPerfectPlus)
        {
            // PerfectPlus maps to bit 3; other tiers use their enum integer value.
            int bit  = (isPerfectPlus && tier == JudgementTier.Perfect) ? 3 : (int)tier;
            int flag = 1 << bit;

            if ((_missingMaterialWarnedMask & flag) != 0) { return; }
            _missingMaterialWarnedMask |= flag;

            string tierName = (isPerfectPlus && tier == JudgementTier.Perfect)
                ? "Perfect+"
                : tier.ToString();

            Debug.LogWarning(
                $"[JudgementFeedbackRenderer] The '{tierName}' judgement entry's material is null.  " +
                $"Assign a material (e.g. Unlit/Transparent) to the '{tierName}' entry in the " +
                "GameplayFeedbackSkinSet.  This tier's effects will not render until a material is assigned.");
        }

        // -------------------------------------------------------------------
        // Unit-quad mesh builder
        // -------------------------------------------------------------------

        // 1×1 quad centered at origin (Z = 0).  Vertices at ±0.5.
        // Sized per effect via TRS scale: TRS scale = 2 × sizeLocal × rootScale.
        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "JudgementFeedback_UnitQuad" };

            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            // Two CCW triangles from +Z view — matches winding of other renderers.
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            return mesh;
        }

        // -------------------------------------------------------------------
        // Debug helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Number of currently active pool slots.  For debug overlays; not hot-path.
        /// </summary>
        public int ActiveEffectCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < PoolSize; i++)
                {
                    if (_pool[i].IsActive) { count++; }
                }
                return count;
            }
        }
    }
}
