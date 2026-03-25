// JudgementFeedbackRenderer.cs
// Production judgement-feedback renderer skeleton (spec §5.11.2).
//
// Listens for judgement events from PlayerAppController and manages a pool of
// short-lived feedback effect instances — one per note hit/miss.
//
// ── What this renderer does (Job 1 — skeleton) ────────────────────────────────
//
//   • Subscribes to PlayerAppController.OnJudgement, .OnHoldTick, and
//     .OnHoldResolved (spec §5.11.2 event contract).
//   • On each event: looks up the correct JudgementFeedbackEntry from the skin,
//     claims a free slot from the pre-allocated pool, and records the spawn data.
//   • Each LateUpdate: advances the age of every active slot and deactivates
//     expired ones (age >= lifetime).
//   • Does NOT yet draw anything — the full spawn/animation/draw path is deferred
//     to Job 2.  The pool lifecycle is production-safe and zero-GC in the hot path.
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT affect input, hit-testing, judgement, or scoring.
//   • Does NOT render anything yet — draw calls are deferred to Job 2.
//   • Does NOT implement the stack-vs-replace policy beyond storing the setting.
//   • Does NOT implement billboard facing or upward-drift motion yet.
//   • Does NOT add hold-specific feedback variants (deferred, spec §5.11.2).
//   • Does NOT depend on PlayerDebugRenderer or any debug component.
//
// ── Pool design ───────────────────────────────────────────────────────────────
//
//   PoolSize slots are pre-allocated in Awake — zero per-frame GC after that.
//   Each slot is an ActiveFeedback struct (value type) tracking:
//     • IsActive — whether the slot is in use
//     • LaneId   — lane that fired the event (for position lookup in Job 2)
//     • Age      — seconds elapsed since spawn
//     • Lifetime — seconds until expiry (copied from the skin entry at spawn time)
//     • Config   — copy of the JudgementFeedbackEntry at spawn time
//     • IsPerfectPlus — from JudgementRecord.IsPerfectPlus (display-only)
//
//   Slot selection:
//     1. Find the first inactive slot.
//     2. If all slots are active (pool full), evict the slot with the highest
//        age-to-lifetime ratio (the one closest to expiry — least visual impact).
//
// ── Event subscription ────────────────────────────────────────────────────────
//
//   Subscription is attempted lazily: OnEnable calls TrySubscribe, and LateUpdate
//   also calls TrySubscribe each frame until it succeeds.  This handles the case
//   where playerAppController is assigned in the Inspector but the component is
//   enabled before the controller's Awake has run (rare but safe to handle).
//   Unsubscribe happens in OnDisable and OnDestroy.
//
// ── Wiring (Job 2 will need these) ───────────────────────────────────────────
//
//   1. Add this component to any GO in the Player scene.
//   2. Assign playerAppController, frustumProfile, and skinSet in the Inspector.
//   3. Assign materials (Unlit/Transparent) to each enabled judgement entry in skinSet.
//   4. The pool is fully managed internally — no further scene setup required.
//
//   NOTE (Job 2): This renderer will need access to playerAppController.LaneGeometries,
//   .ArenaGeometries, and .PlayfieldTf to compute the effect spawn position at the
//   judgement ring along the lane's center angle.
//
// ── Spec reference ────────────────────────────────────────────────────────────
//
//   Spec §5.11.2 (judgement feedback design rules) / §5.12 (skin contract).

using UnityEngine;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production judgement-feedback renderer skeleton (spec §5.11.2).
    /// Manages a pre-allocated pool of judgement effect instances; draws nothing yet
    /// — the full spawn/animation path is implemented in Job 2.
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

        [Tooltip("Shared frustum profile.  Used by Job 2 to place the effect at the correct\n" +
                 "Z height on the arena cone surface.\n\n" +
                 "When null or UseFrustumProfile is false, Job 2 will default to Z = 0.001 (flat).")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Tooltip("Gameplay feedback skin.  Source of truth for all judgement effect appearance.\n\n" +
                 "Entries: perfectPlus / perfect / great / miss — each with material, tint, size,\n" +
                 "lifetime, fade, drift, and enabled flag.\n" +
                 "stackPolicy controls how concurrent effects interact.")]
        [SerializeField] private GameplayFeedbackSkinSet skinSet;

        // -------------------------------------------------------------------
        // Pool settings
        // -------------------------------------------------------------------

        // Maximum number of simultaneously active judgement effects.
        // 32 slots comfortably covers rapid note streams without over-allocating.
        // Changing this constant requires a recompile; pool is fixed after Awake.
        private const int PoolSize = 32;

        // -------------------------------------------------------------------
        // Pool element — one active judgement effect instance
        //
        // Stored as a struct (value type) to avoid per-element heap allocation.
        // The pool array itself is allocated once in Awake and never resized.
        // -------------------------------------------------------------------
        private struct ActiveFeedback
        {
            /// <summary>True when this slot is holding a live effect.</summary>
            public bool IsActive;

            /// <summary>
            /// Seconds elapsed since this effect was spawned.
            /// Incremented each LateUpdate by Time.deltaTime.
            /// </summary>
            public float Age;

            /// <summary>
            /// Total lifetime of this effect in seconds (copied from Config.lifetime
            /// at spawn time so it remains stable even if the skin asset changes).
            /// </summary>
            public float Lifetime;

            /// <summary>
            /// LaneId of the note that triggered this effect.
            /// Used by Job 2 to look up lane geometry for spawn position.
            /// </summary>
            public string LaneId;

            /// <summary>
            /// Snapshot of the skin entry at spawn time.
            /// Stored so animation values remain consistent for the full lifetime
            /// even if the skin asset is edited while the effect is playing.
            /// </summary>
            public JudgementFeedbackEntry Config;

            /// <summary>
            /// True when this effect was triggered by a Perfect+ hit (spec §4.3).
            /// Display-only; no score change.  Passed through so Job 2 can use it
            /// for any additional visual treatment (e.g. extra sparkle).
            /// </summary>
            public bool IsPerfectPlus;
        }

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // The pre-allocated pool.  Allocated once in Awake; never resized.
        private ActiveFeedback[] _pool;

        // Tracks whether we have successfully subscribed to the controller events.
        // TrySubscribe() is called both in OnEnable and each LateUpdate until true.
        private bool _subscribed;

        // Once-fired warning flags — log once per misconfiguration, then go silent.
        private bool _hasWarnedMissingController;
        private bool _hasWarnedMissingSkinSet;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Allocate the pool. All slots default to IsActive = false.
            _pool = new ActiveFeedback[PoolSize];
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
        }

        // -------------------------------------------------------------------
        // Per-frame update
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            // Lazy subscription: keep trying until playerAppController is available.
            TrySubscribe();

            // Advance the age of every active slot; deactivate expired ones.
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { continue; }

                _pool[i].Age += Time.deltaTime;

                if (_pool[i].Age >= _pool[i].Lifetime)
                {
                    _pool[i].IsActive = false;
                }
            }

            // TODO Job 2: iterate active slots and draw each effect via
            // Graphics.DrawMesh + MaterialPropertyBlock, positioned at the
            // judgement ring along the lane's center angle with upward drift
            // applied as Z offset = Config.driftSpeedLocal * Age.
        }

        // -------------------------------------------------------------------
        // Event subscription helpers
        // -------------------------------------------------------------------

        private void TrySubscribe()
        {
            if (_subscribed) { return; }
            if (playerAppController == null) { return; }

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
        // Hold-specific visual variants are deferred; the standard per-tier
        // visual is used for now (see spec §5.11.2 — hold variants reserved).
        private void HandleHoldTick(JudgementRecord r)
        {
            SpawnEffect(r);
        }

        // Handles hold-resolved judgement (Perfect — held through end; Miss —
        // never bound).  Hold-specific visual variants are deferred.
        private void HandleHoldResolved(JudgementRecord r)
        {
            SpawnEffect(r);
        }

        // -------------------------------------------------------------------
        // Pool spawn
        // -------------------------------------------------------------------

        // Looks up the skin entry for the record, finds (or evicts) a pool slot,
        // and records the spawn data.  Zero allocation in the hot path after Awake.
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
            // GetJudgementEntryForTier handles the Perfect+ override (when enabled)
            // and falls back to the standard tier entry otherwise.
            JudgementFeedbackEntry entry = skinSet.GetJudgementEntryForTier(r.Tier, r.IsPerfectPlus);

            // A disabled entry is a valid no-op (e.g. silent miss by default).
            // Do NOT log a warning here — disabled is intentional.
            if (!entry.enabled) { return; }

            // ── Claim a pool slot ──────────────────────────────────────────────────
            int slot = FindFreeSlot();

            // ── Write spawn data ───────────────────────────────────────────────────
            // Use direct field assignment to avoid any allocation.
            _pool[slot].IsActive     = true;
            _pool[slot].Age          = 0f;
            _pool[slot].Lifetime     = entry.lifetime;
            _pool[slot].LaneId       = r.Note.LaneId;   // string reference copy — no allocation
            _pool[slot].Config       = entry;            // struct copy — no allocation
            _pool[slot].IsPerfectPlus = r.IsPerfectPlus;

            // TODO Job 2: compute and store the world-space spawn position here
            // using playerAppController.LaneGeometries, .ArenaGeometries, and
            // .PlayfieldTf — same geometry source as LaneTouchFeedbackRenderer.
        }

        // -------------------------------------------------------------------
        // Pool slot selection
        // -------------------------------------------------------------------

        // Returns the index of a free slot, or the index of the slot closest
        // to expiry (highest age/lifetime ratio) when the pool is full.
        // This is an O(n) scan over PoolSize (32) slots — negligible cost.
        private int FindFreeSlot()
        {
            // First pass: prefer a completely inactive slot.
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].IsActive) { return i; }
            }

            // Pool is full: evict the slot with the highest age/lifetime ratio
            // (the one closest to expiry has the least remaining visual impact).
            int   oldestSlot  = 0;
            float maxRatio    = -1f;

            for (int i = 0; i < PoolSize; i++)
            {
                float ratio = _pool[i].Lifetime > 0f
                    ? _pool[i].Age / _pool[i].Lifetime
                    : 1f;

                if (ratio > maxRatio)
                {
                    maxRatio   = ratio;
                    oldestSlot = i;
                }
            }

            return oldestSlot;
        }

        // -------------------------------------------------------------------
        // Debug helpers
        // -------------------------------------------------------------------

        // Returns the number of currently active pool slots.
        // Intended for debug overlays and editor tooling — not called in the
        // production hot path.
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
