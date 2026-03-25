// JudgementFeedbackRenderer.cs
// Production judgement-feedback renderer (spec §5.11.2).
//
// ── What this renderer does ───────────────────────────────────────────────────
//
//   Job 1 (skeleton — unchanged):
//     • Subscribes to PlayerAppController.OnJudgement, .OnHoldTick, .OnHoldResolved.
//     • Manages a pre-allocated pool of 32 ActiveFeedback slots.
//     • On each event: looks up the correct JudgementFeedbackEntry from the skin,
//       claims a free pool slot, and records spawn data.
//     • Each LateUpdate: advances ages and expires elapsed slots.
//
//   Job 2 (this step — spawn position, billboard, animation, draw):
//     • Computes the world-space spawn position at the lane's judgement ring point:
//         – XY: arena center + cos/sin(lane.CenterDeg) × judgementRadius
//         – Z:  FrustumZAtRadius(judgementR) + entry.surfaceOffsetLocal
//       Stored in SpawnPositionLocal (pfRoot local space) at spawn time.
//       Position is stable even if arena/lane geometry animates after spawn.
//     • Each LateUpdate draws each active slot as a world-space camera-facing quad:
//         – Drift:   localPos.z += entry.driftSpeedLocal × age  (above-surface float)
//         – Billboard: Quaternion.LookRotation(camPos − worldPos, camUp)
//                      so the quad's +Z faces the camera.
//         – Pop/scale: starts at 60% size, grows to 100% over the first 15% of
//                      lifetime (quick punch-in without an extra skin parameter).
//         – Fade-out: full opacity until (lifetime − fadeOutDuration), then
//                     linear fade from entry.opacity to 0.
//     • Draws via Graphics.DrawMesh + one shared unit-quad mesh +
//       one reused MaterialPropertyBlock.  Zero GC alloc in the hot path.
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT affect input, hit-testing, judgement, or scoring.
//   • Does NOT implement stack-vs-replace runtime policy (deferred to Job 3).
//   • Does NOT add hold-specific visual families (deferred, spec §5.11.2).
//   • Does NOT depend on PlayerDebugRenderer or any debug component.
//
// ── Spawn position formula ────────────────────────────────────────────────────
//
//   In pfRoot local space:
//     x     = arenaCenter.x + cos(laneGeo.CenterDeg) × judgementR
//     y     = arenaCenter.y + sin(laneGeo.CenterDeg) × judgementR
//     z     = FrustumZAtRadius(judgementR, innerLocal, outerLocal, hInner, hOuter)
//             + entry.surfaceOffsetLocal
//
//   This is the same geometry source as LaneTouchFeedbackRenderer and
//   JudgementRingRenderer — not camera-specific, correct under camera motion.
//
// ── Billboard facing ──────────────────────────────────────────────────────────
//
//   Computed per frame in LateUpdate:
//     1. currentLocalPos = SpawnPositionLocal + Vector3.forward × (driftSpeed × age)
//     2. worldPos  = pfRoot.TransformPoint(currentLocalPos)
//     3. toCam     = Camera.main.position − worldPos
//     4. billboardRot = Quaternion.LookRotation(toCam, Camera.main.up)
//     5. TRS matrix  = Matrix4x4.TRS(worldPos, billboardRot, Vector3.one × worldScale)
//
//   The local unit-quad (±0.5 in XY, Z=0) is drawn in world space via this TRS.
//   Camera is cached and refreshed when null (handles scene reload).
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   • One shared unit-quad Mesh, pre-built in Awake, destroyed in OnDestroy.
//   • One shared MaterialPropertyBlock, cleared before each draw call.
//   • Each active slot calls Graphics.DrawMesh once per LateUpdate.
//   • The material from the skin entry is the template shader; _Color and
//     _MainTex are overridden per call via MaterialPropertyBlock.
//
// ── Wiring ────────────────────────────────────────────────────────────────────
//
//   1. Add this component to any GO in the Player scene.
//   2. Assign playerAppController, frustumProfile, and skinSet in the Inspector.
//   3. Assign a material (e.g. Unlit/Transparent) to each enabled judgement entry
//      in the skinSet.  A null material skips that tier with a once-only warning.
//   4. Optionally assign textures to each entry for label/sprite looks.
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
                 "lifetime, fade, drift, and enabled flag.\n" +
                 "stackPolicy is stored but not yet enforced (deferred to Job 3).")]
        [SerializeField] private GameplayFeedbackSkinSet skinSet;

        // -------------------------------------------------------------------
        // Animation constants
        // -------------------------------------------------------------------

        // Maximum number of simultaneously active judgement effects.
        // 32 slots handles rapid note streams without over-allocating.
        private const int PoolSize = 32;

        // Pop scale animation:
        //   First PopFraction of the lifetime: scale grows from PopStartScale to 1.0.
        //   Remaining lifetime: scale stays at 1.0 while opacity fades.
        // These are intentionally not skin parameters to keep the skin contract minimal.
        private const float PopFraction   = 0.15f;
        private const float PopStartScale = 0.60f;

        // -------------------------------------------------------------------
        // Pool element — one active judgement effect instance
        //
        // Struct (value type) — the pool array is allocated once in Awake
        // and never resized, so zero per-element GC after startup.
        // -------------------------------------------------------------------
        private struct ActiveFeedback
        {
            /// <summary>True when this slot is holding a live effect.</summary>
            public bool IsActive;

            /// <summary>Seconds elapsed since spawn.  Incremented by Time.deltaTime each LateUpdate.</summary>
            public float Age;

            /// <summary>
            /// Total lifetime in seconds (snapshot of Config.lifetime at spawn time).
            /// Stored separately so the skin can be hot-reloaded without mid-flight changes.
            /// </summary>
            public float Lifetime;

            /// <summary>LaneId of the note that triggered this effect (string ref — no allocation).</summary>
            public string LaneId;

            /// <summary>
            /// Snapshot of the skin entry at spawn time.
            /// Animation values (size, fade, drift, tint) are read from this copy, not
            /// from the live skin asset, so they remain consistent for the full lifetime.
            /// </summary>
            public JudgementFeedbackEntry Config;

            /// <summary>True when triggered by a Perfect+ hit (spec §4.3, display-only).</summary>
            public bool IsPerfectPlus;

            /// <summary>
            /// Effect origin in pfRoot local space at Age = 0.
            /// Includes arena center offset, judgement radius, and surfaceOffsetLocal.
            /// The drift component (driftSpeedLocal × age) is added each frame at draw time.
            /// </summary>
            public Vector3 SpawnPositionLocal;
        }

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Pre-allocated pool.  Allocated once in Awake, never resized.
        private ActiveFeedback[] _pool;

        // Single shared unit-quad mesh (built in Awake, destroyed in OnDestroy).
        // Vertices at ±0.5 in local XY (Z = 0).  Sized via the TRS scale in DrawMesh.
        private Mesh _quadMesh;

        // Single reused MaterialPropertyBlock.  Cleared before each draw call to
        // prevent texture/color bleed between slots with different entries.
        private MaterialPropertyBlock _propBlock;

        // Cached Camera.main reference.  Re-fetched when null (handles scene reload
        // or delayed camera startup).
        private Camera _mainCamera;

        // Whether we have successfully subscribed to the controller events.
        // TrySubscribe() is called in OnEnable and lazily each LateUpdate.
        private bool _subscribed;

        // Once-fired warning flags.
        private bool _hasWarnedMissingSkinSet;
        private bool _hasWarnedGeometryNotFound;

        // Per-tier missing-material mask.
        // Bit layout matches JudgementTier enum values plus one extra bit for PerfectPlus:
        //   bit 0 = Miss (JudgementTier.Miss   = 0)
        //   bit 1 = Great (JudgementTier.Great  = 1)
        //   bit 2 = Perfect (JudgementTier.Perfect = 2)
        //   bit 3 = PerfectPlus (isPerfectPlus && tier == Perfect)
        private int _missingMaterialWarnedMask;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Pre-allocate the pool.  All slots default to IsActive = false.
            _pool = new ActiveFeedback[PoolSize];

            // Single shared MaterialPropertyBlock — reused for every draw call.
            _propBlock = new MaterialPropertyBlock();

            // Single shared unit-quad mesh — used for every billboard draw call.
            _quadMesh = BuildUnitQuad();
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        private void OnDisable()
        {
            TryUnsubscribe();
        }

        private void OnDestroy()
        {
            TryUnsubscribe();

            // Clean up the mesh we own.  Other renderers' assets are not touched.
            if (_quadMesh != null) { Destroy(_quadMesh); _quadMesh = null; }
        }

        // -------------------------------------------------------------------
        // Per-frame update
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            // Lazy subscription: keep trying until playerAppController is available.
            TrySubscribe();

            // Re-fetch camera if lost (e.g. after scene reload or delayed startup).
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
            if (_mainCamera == null)           { return; } // camera not yet available
            if (_quadMesh  == null)            { return; } // should not happen after Awake
            if (playerAppController == null)   { return; }

            Transform pfRoot = playerAppController.playfieldRoot;
            if (pfRoot == null) { return; }

            // pfRoot.lossyScale.x converts PlayfieldLocal units to world units.
            // Assumes uniform scale on pfRoot (standard setup).
            float   rootScale = pfRoot.lossyScale.x;
            Vector3 camPos    = _mainCamera.transform.position;
            Vector3 camUp     = _mainCamera.transform.up;

            // ── Draw active slots ─────────────────────────────────────────────────
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { continue; }

                float                  age      = _pool[i].Age;
                float                  lifetime = _pool[i].Lifetime;
                JudgementFeedbackEntry cfg      = _pool[i].Config;      // struct copy, no alloc

                // Material check — we skip at spawn time when null, but defend against
                // hot-reload or asset mutation between spawn and draw.
                if (cfg.material == null) { continue; }

                // ── Current position: base + upward drift ──────────────────────────
                // Drift is in pfRoot local +Z (the axis that points above the arena
                // cone surface).  Converting through pfRoot.TransformPoint keeps the
                // drift direction correct under any playfieldRoot orientation.
                Vector3 localPos = _pool[i].SpawnPositionLocal;
                localPos.z += cfg.driftSpeedLocal * age;
                Vector3 worldPos = pfRoot.TransformPoint(localPos);

                // ── Camera-facing billboard rotation ───────────────────────────────
                // Make the quad's local +Z axis point toward the camera so the face
                // is always visible regardless of camera position or playfieldRoot tilt.
                Vector3 toCam = camPos - worldPos;
                if (toCam.sqrMagnitude < 1e-6f) { continue; } // camera inside effect — degenerate
                Quaternion billboardRot = Quaternion.LookRotation(toCam, camUp);

                // ── Pop scale ──────────────────────────────────────────────────────
                // Quick scale-up from PopStartScale to 1.0 during the first PopFraction
                // of lifetime.  Gives a punchy spawn feel without a skin parameter.
                float popDuration = lifetime * PopFraction;
                float popScale = (popDuration > 0f && age < popDuration)
                    ? Mathf.Lerp(PopStartScale, 1.0f, age / popDuration)
                    : 1.0f;

                // ── Fade-out alpha ─────────────────────────────────────────────────
                // Full opacity from spawn until (lifetime − fadeOutDuration), then
                // linear fade to 0.  Clamped so rounding errors don't break alpha.
                float fadeStartTime = lifetime - cfg.fadeOutDuration;
                float fadeAlpha = (cfg.fadeOutDuration > 0f && age >= fadeStartTime)
                    ? 1f - (age - fadeStartTime) / cfg.fadeOutDuration
                    : 1f;
                fadeAlpha = Mathf.Clamp01(fadeAlpha);

                // ── World-space quad scale ─────────────────────────────────────────
                // cfg.sizeLocal = half-extent in PlayfieldLocal units.
                // The unit quad has local half-extent 0.5, so:
                //   TRS scale = 2 × sizeLocal × rootScale × popScale
                // This yields a world half-extent = sizeLocal × rootScale.
                float worldScale = 2f * cfg.sizeLocal * rootScale * popScale;
                if (worldScale <= 0f) { continue; } // degenerate size — skip

                // ── MaterialPropertyBlock ──────────────────────────────────────────
                // Clear removes all properties from the previous slot so texture/color
                // from one tier cannot bleed into another tier's draw call.
                _propBlock.Clear();

                // Final tint = skin tint × (skin opacity × fade alpha).
                Color drawColor = cfg.tint;
                drawColor.a = cfg.tint.a * cfg.opacity * fadeAlpha;
                _propBlock.SetColor("_Color", drawColor);

                // Texture is optional — null means solid-color tint only.
                if (cfg.texture != null)
                {
                    _propBlock.SetTexture("_MainTex", cfg.texture);
                }

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
            if (_subscribed)                     { return; }
            if (playerAppController == null)     { return; }

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

        // Handles a scored tap / catch / flick / sweep-miss judgement.
        private void HandleJudgement(JudgementRecord r)
        {
            SpawnEffect(r);
        }

        // Handles one hold-tick judgement (Perfect or Miss, spec §7.5.1).
        // Hold-specific visual variants are deferred (spec §5.11.2).
        // For now, the standard per-tier visual is used.
        private void HandleHoldTick(JudgementRecord r)
        {
            SpawnEffect(r);
        }

        // Handles hold-resolved judgement (Perfect — held through; Miss — unbound).
        // Hold-specific visual variants are deferred.
        private void HandleHoldResolved(JudgementRecord r)
        {
            SpawnEffect(r);
        }

        // -------------------------------------------------------------------
        // Pool spawn
        // -------------------------------------------------------------------

        // Looks up the skin entry, validates it, computes the spawn position,
        // and activates a pool slot.  Zero allocation after initial setup.
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

            // ── Look up the correct skin entry ─────────────────────────────────────
            // GetJudgementEntryForTier handles the optional Perfect+ override.
            JudgementFeedbackEntry entry = skinSet.GetJudgementEntryForTier(r.Tier, r.IsPerfectPlus);

            // A disabled entry is a valid no-op (e.g. silent miss by default).
            // Do NOT log a warning — disabled is intentional.
            if (!entry.enabled) { return; }

            // ── Guard: material required to draw ───────────────────────────────────
            // If material is null, skip the spawn and warn once per tier.
            if (entry.material == null)
            {
                WarnMissingMaterial(r.Tier, r.IsPerfectPlus);
                return;
            }

            // ── Compute spawn position ─────────────────────────────────────────────
            // Evaluates the lane's judgement ring point from current geometry.
            // Geometry dictionaries are populated by PlayerAppController.Update() before
            // events fire, so this should succeed during normal gameplay.
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

            // ── Claim a pool slot ──────────────────────────────────────────────────
            int slot = FindFreeSlot();

            // ── Write spawn data ───────────────────────────────────────────────────
            // All assignments are value copies or reference copies — no allocation.
            _pool[slot].IsActive           = true;
            _pool[slot].Age                = 0f;
            _pool[slot].Lifetime           = entry.lifetime;
            _pool[slot].LaneId             = r.Note.LaneId;     // string ref copy — no alloc
            _pool[slot].Config             = entry;             // struct copy — no alloc
            _pool[slot].IsPerfectPlus      = r.IsPerfectPlus;
            _pool[slot].SpawnPositionLocal = spawnLocalPos;
        }

        // -------------------------------------------------------------------
        // Spawn position helper
        // -------------------------------------------------------------------

        // Computes the spawn position in pfRoot local space at the lane's
        // judgement ring point.  Returns false if any required lookup fails.
        //
        // Position:
        //   x = arenaCenter.x + cos(laneGeo.CenterDeg) × judgementR
        //   y = arenaCenter.y + sin(laneGeo.CenterDeg) × judgementR
        //   z = FrustumZAtRadius(judgementR, …) + surfaceOffsetLocal
        //
        // Uses the same geometry source as LaneTouchFeedbackRenderer.
        private bool TryComputeSpawnPosition(string laneId, float surfaceOffsetLocal,
                                             out Vector3 spawnLocalPos)
        {
            spawnLocalPos = Vector3.zero;

            // Geometry dictionaries.
            var laneGeos    = playerAppController.LaneGeometries;
            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneToArena = playerAppController.LaneToArena;
            PlayfieldTransform pfT = playerAppController.PlayfieldTf;

            if (laneGeos == null || arenaGeos == null || laneToArena == null || pfT == null)
            {
                return false;
            }

            // Look up lane and arena geometry.
            if (!laneGeos.TryGetValue(laneId, out LaneGeometry laneGeo))    { return false; }
            if (!laneToArena.TryGetValue(laneId, out string arenaId))       { return false; }
            if (!arenaGeos.TryGetValue(arenaId, out ArenaGeometry arenaGeo)) { return false; }

            // Arena radii in PlayfieldLocal units.
            float outerLocal = pfT.NormRadiusToLocal(arenaGeo.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(arenaGeo.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Judgement ring radius — same formula as LaneTouchFeedbackRenderer and
            // JudgementRingRenderer (spec §5.8).
            float judgementR = NoteApproachMath.JudgementRadius(
                outerLocal, pfT.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);

            // Frustum Z at the judgement ring — lifts the effect onto the cone surface.
            // Falls back to near-zero flat Z if no frustum profile is assigned.
            bool  useProfile = frustumProfile != null && frustumProfile.UseFrustumProfile;
            float hInner     = useProfile ? frustumProfile.FrustumHeightInner : 0.001f;
            float hOuter     = useProfile ? frustumProfile.FrustumHeightOuter : 0.001f;
            float surfaceZ   = NoteApproachMath.FrustumZAtRadius(
                judgementR, innerLocal, outerLocal, hInner, hOuter);

            // Arena center in PlayfieldLocal space.
            Vector2 center = pfT.NormalizedToLocal(
                new Vector2(arenaGeo.CenterXNorm, arenaGeo.CenterYNorm));

            // XY position: on the judgement ring at the lane's center angle.
            float angleRad = laneGeo.CenterDeg * Mathf.Deg2Rad;
            float x        = center.x + Mathf.Cos(angleRad) * judgementR;
            float y        = center.y + Mathf.Sin(angleRad) * judgementR;

            // Z: cone surface height + entry's above-surface offset.
            float z = surfaceZ + surfaceOffsetLocal;

            spawnLocalPos = new Vector3(x, y, z);
            return true;
        }

        // -------------------------------------------------------------------
        // Pool slot selection
        // -------------------------------------------------------------------

        // Returns the index of a free (inactive) slot, or the index of the slot
        // closest to expiry (highest age/lifetime ratio) when the pool is full.
        // O(n) over PoolSize (32) — negligible cost.
        private int FindFreeSlot()
        {
            // First pass: prefer a completely inactive slot.
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { return i; }
            }

            // Pool is full — evict the slot with the highest age/lifetime ratio
            // (closest to expiry = least remaining visual impact on screen).
            int   evictSlot = 0;
            float maxRatio  = -1f;

            for (int i = 0; i < PoolSize; i++)
            {
                float ratio = _pool[i].Lifetime > 0f
                    ? _pool[i].Age / _pool[i].Lifetime
                    : 1f;

                if (ratio > maxRatio)
                {
                    maxRatio   = ratio;
                    evictSlot  = i;
                }
            }

            return evictSlot;
        }

        // -------------------------------------------------------------------
        // Missing-material warning helper
        // -------------------------------------------------------------------

        // Logs a once-only warning per judgement tier when a material is null.
        // Uses a bitmask to avoid repeated warnings for the same tier.
        private void WarnMissingMaterial(JudgementTier tier, bool isPerfectPlus)
        {
            // PerfectPlus is not a JudgementTier enum value; map it to bit 3.
            int bit  = (isPerfectPlus && tier == JudgementTier.Perfect) ? 3 : (int)tier;
            int flag = 1 << bit;

            if ((_missingMaterialWarnedMask & flag) != 0) { return; } // already warned for this tier
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

        // Builds a 1×1 flat quad centered at the local origin (Z = 0).
        // Vertices at ±0.5 so the TRS scale maps directly to world half-extents.
        // This is the only mesh this renderer draws; it is scaled per effect via TRS.
        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "JudgementFeedback_UnitQuad" };

            // Quad vertices in the local XY plane, centered at origin.
            // +X = right, +Y = up, quad faces +Z (toward camera after billboard rotation).
            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),   // bottom-left
                new Vector3( 0.5f, -0.5f, 0f),   // bottom-right
                new Vector3( 0.5f,  0.5f, 0f),   // top-right
                new Vector3(-0.5f,  0.5f, 0f),   // top-left
            };

            // Standard UV mapping: (0,0) bottom-left, (1,1) top-right.
            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            // Two CCW triangles covering the quad (CCW from +Z view, matches other renderers).
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateBounds();

            return mesh;
        }

        // -------------------------------------------------------------------
        // Debug helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the number of currently active pool slots.
        /// Intended for debug overlays — not called in the production hot path.
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
