// GameplayFeedbackSkinSet.cs
// ScriptableObject data container for production feedback visuals.
//
// ── What this asset controls ──────────────────────────────────────────────────
//
//   GameplayFeedbackSkinSet controls feedback APPEARANCE only:
//     – lane-touch highlight visuals (while a lane is actively touched)
//     – judgement feedback visuals (Perfect+ / Perfect / Great / Miss result display)
//
//   It does NOT control:
//     – note, hold, or arrow body visuals      (see NoteSkinSet)
//     – arena surface fill layers              (see ArenaSurfaceSkinSet)
//     – frustum geometry or cone heights       (see PlayfieldFrustumProfile)
//     – gameplay timing or judgement logic     (see JudgementEngine)
//     – input ownership or colliders           (see ArenaColliderProvider)
//     – debug overlays or scaffolding          (see PlayerDebugRenderer)
//
// ── Section overview ──────────────────────────────────────────────────────────
//
//   LaneTouchFeedback
//     A single config block for the lane-touch highlight: the subtle visual
//     that activates while a player's finger touches an active lane.
//     Rendered by LaneTouchFeedbackRenderer (§5.11.1).
//
//   JudgementStackPolicy  (enum)
//     Controls how multiple overlapping judgement effects interact:
//       Stack   — effects accumulate; several can be alive for the same lane.
//       Replace — a new effect for a lane immediately deactivates any prior effect.
//
//   JudgementFeedbackEntry  (struct, reused for Perfect+ / Perfect / Great / Miss)
//     Per-judgement config block.  All four entries share the same field layout.
//     Miss is intentionally disabled by default — a disabled Miss entry means
//     "consume the note with no visual effect", which is a valid no-op design.
//     Rendered by JudgementFeedbackRenderer (§5.11.2).
//
//   GameplayFeedbackSkinSet judgement block:
//     – useSeparatePerfectPlusVisual: when true, Perfect+ hits use the perfectPlus
//       entry instead of the perfect entry.  When false, Perfect+ shares perfect.
//     – perfectPlus, perfect, great, miss: per-tier entries.
//     – stackPolicy: Stack or Replace (see JudgementStackPolicy).
//
// ── Hold-specific feedback ────────────────────────────────────────────────────
//
//   Hold-specific feedback variants (e.g. hold-tick pulses, hold-release flash)
//   are deferred.  A reserved comment section is provided below to document the
//   planned expansion point.  Do not add fields there until the renderer is
//   specified.
//
// ── Material template pattern ─────────────────────────────────────────────────
//
//   Same convention as NoteSkinSet and ArenaSurfaceSkinSet:
//     – Materials are shader templates; do NOT bake textures into them.
//     – Renderers assign _MainTex and _Color at draw time via MaterialPropertyBlock.
//     – Unlit/Transparent is sufficient for most feedback looks.
//
// ── Authoring workflow ────────────────────────────────────────────────────────
//
//   1. Create via  Assets → Create → RhythmicFlow → Gameplay Feedback Skin Set.
//   2. Assign material templates (Unlit/Transparent) to each enabled section.
//   3. Optionally assign textures; leave null for solid-color tint-only looks.
//   4. Set tint, opacity, size, and timing fields per section.
//   5. Leave miss.enabled = false for a silent "no miss effect" design.
//   6. Enable useSeparatePerfectPlusVisual and configure perfectPlus if you want
//      a distinct Perfect+ look (e.g. brighter flash for the tightest window).
//   7. Set stackPolicy to Replace if you want new effects to cancel old ones.
//   8. Assign this asset to LaneTouchFeedbackRenderer / JudgementFeedbackRenderer
//      in the Inspector.
//
// ── Spec reference ────────────────────────────────────────────────────────────
//
//   Spec §5.11 (production feedback subsystems) / §5.12 (GameplayFeedbackSkinSet).

using UnityEngine;

namespace RhythmicFlow.Player
{
    // -------------------------------------------------------------------------
    // JudgementStackPolicy — how concurrent judgement effects interact
    //
    // Controls the renderer's behaviour when a new judgement fires while one or
    // more effects are already alive for the same lane.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Controls how <c>JudgementFeedbackRenderer</c> handles overlapping effects
    /// for the same lane (spec §5.11.2).
    ///
    /// <para><b>Stack</b> — multiple effects can be alive simultaneously for the
    /// same lane.  Rapid note streams produce layered bursts.</para>
    ///
    /// <para><b>Replace</b> — a new effect immediately deactivates any still-active
    /// effect on the same lane, then starts fresh.  Keeps the display uncluttered
    /// during dense passages.</para>
    ///
    /// <para>The renderer stores this setting at init time; runtime policy switching
    /// mid-song is intentionally not supported in v0.</para>
    /// </summary>
    public enum JudgementStackPolicy
    {
        /// <summary>Effects accumulate: several can be alive for the same lane at once.</summary>
        Stack,

        /// <summary>A new effect replaces any prior active effect on the same lane.</summary>
        Replace,
    }

    // -------------------------------------------------------------------------
    // LaneTouchFeedback — serializable config for the lane-touch highlight
    //
    // Activated while a player's finger is in contact with an active lane.
    // Intended as a subtle, non-distracting confirmation of touch presence —
    // not a scored event.  Separate from JudgementFeedbackEntry (which fires
    // on note hit/miss) and from note body rendering.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Configuration for the lane-touch highlight: the production feedback
    /// visual active while a lane is being touched.
    ///
    /// <para>Controls appearance only.  Consumed by <c>LaneTouchFeedbackRenderer</c>
    /// (planned, §5.11.1).  Separate from judgement feedback and note body rendering.</para>
    /// </summary>
    [System.Serializable]
    public struct LaneTouchFeedback
    {
        [Tooltip("When false, no lane-touch highlight is rendered for any lane.\n\n" +
                 "Disabling this at the asset level suppresses the effect entirely — " +
                 "the renderer omits all draw calls for this section.")]
        public bool enabled;

        [Tooltip("Shader template for the lane-touch highlight quad.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex via MaterialPropertyBlock.\n" +
                 "  • Must support _Color via MaterialPropertyBlock (for tint × opacity).\n" +
                 "  • Unlit/Transparent is sufficient.\n\n" +
                 "Do NOT bake a texture into this material.  One material can be shared " +
                 "across all lane highlight instances.")]
        public Material material;

        [Tooltip("(Optional) Texture assigned to _MainTex via MaterialPropertyBlock.\n\n" +
                 "Leave null for a solid-color highlight driven by tint alone.\n" +
                 "A soft radial gradient or lane-width-spanning glow texture works well here.")]
        public Texture2D texture;

        [Tooltip("Color tint for the lane-touch highlight.\n\n" +
                 "Final shader color = tint × (alpha replaced by tint.a × opacity).\n" +
                 "Use a light, semi-transparent tint (e.g. white at alpha 0.4) for a subtle look.")]
        public Color tint;

        [Tooltip("Opacity of the highlight at full strength [0..1].\n\n" +
                 "This is the peak opacity reached after fadeInDuration.  The renderer " +
                 "interpolates from 0 to this value on touch-down, and from this value " +
                 "back to 0 over fadeOutDuration on touch-up.\n" +
                 "Default: 1.0")]
        [Range(0f, 1f)]
        public float opacity;

        [Tooltip("Width of the highlight as a fraction of the lane's angular span at the touch radius.\n\n" +
                 "1.0 = fills the full lane width.  0.8 = 80% of lane width.\n" +
                 "Values above 1.0 spill slightly beyond the lane edges for a softer look.\n" +
                 "Default: 1.0")]
        [Range(0.1f, 2f)]
        public float laneWidthScale;

        [Tooltip("Radial extent of the highlight in PlayfieldLocal units.\n\n" +
                 "Used when fullLaneCoverage is false (default mode).\n" +
                 "Controls how deep the highlight reaches from the judgement ring inward.\n" +
                 "A small value (e.g. 0.05) produces a thin highlight near the judgement ring.\n" +
                 "Ignored when fullLaneCoverage is true.\n" +
                 "Default: 0.05")]
        [Min(0.005f)]
        public float radialExtentLocal;

        [Tooltip("When true, the highlight covers the full visible lane:\n" +
                 "  inner edge = innerLocal        (arena inner edge)\n" +
                 "  outer edge = visualOuterLocal   (same rim as ArenaSurfaceRenderer)\n\n" +
                 "When false (default), the highlight covers only the near-judgement band:\n" +
                 "  inner edge = max(innerLocal, judgementRadius - radialExtentLocal)\n" +
                 "  outer edge = judgementRadius\n\n" +
                 "Use false for a subtle ring-edge glow near the judgement line.\n" +
                 "Use true for a full-lane reach-in look that matches the visible arena surface.\n\n" +
                 "Full-lane mode supports gradient textures:\n" +
                 "  V = 1 at the outer (visual rim) edge — should be transparent or faint.\n" +
                 "  V = 0 at the inner edge — same convention.\n" +
                 "Assign a radial gradient texture fading from opaque at mid-V to transparent\n" +
                 "at both edges for a glow-band look.")]
        public bool fullLaneCoverage;

        [Tooltip("Height in PlayfieldLocal Z units to lift the overlay above the arena cone surface.\n\n" +
                 "The renderer places each arc at the frustum cone height for its radius, then\n" +
                 "lifts it above the surface by this amount:\n" +
                 "  Z at inner arc = FrustumZ(highlightInner) + overlayHeightLocal\n" +
                 "  Z at outer arc = FrustumZ(highlightOuter) + overlayHeightLocal\n\n" +
                 "The sector follows the cone surface (same mapping as ArenaSurfaceRenderer)\n" +
                 "and remains visually correct from any camera angle.\n\n" +
                 "Larger values float the overlay higher above the surface (recommended: 0.005–0.02).\n" +
                 "0 = flush with the cone surface (not recommended: may Z-fight arena layers).\n" +
                 "Default: 0.02")]
        [Min(0f)]
        public float overlayHeightLocal;

        [Tooltip("Duration in seconds to fade the highlight in when a touch begins.\n\n" +
                 "0 = instant on.\n" +
                 "Small values (0.03–0.08 s) feel responsive without being jarring.\n" +
                 "Default: 0.05")]
        [Min(0f)]
        public float fadeInDuration;

        [Tooltip("Duration in seconds to fade the highlight out after the touch ends.\n\n" +
                 "0 = instant off.\n" +
                 "Slightly longer than fadeInDuration feels natural (e.g. 0.10–0.15 s).\n" +
                 "Default: 0.12")]
        [Min(0f)]
        public float fadeOutDuration;
    }

    // -------------------------------------------------------------------------
    // JudgementFeedbackEntry — serializable config for one judgement result
    //
    // Reused for all three judgement tiers: Perfect, Great, Miss.
    //
    // Miss is intentionally designed to support enabled = false.
    // A disabled Miss entry means "consume the note with no visual feedback" —
    // this is a valid, intentional no-op design choice, not an error state.
    // The renderer checks enabled before spawning any effect; when false, it
    // skips all work for that judgement tier without warning or logging.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Configuration for one judgement tier's feedback visual (Perfect, Great, or Miss).
    ///
    /// <para>Set <see cref="enabled"/> to <c>false</c> to produce a "silent" judgement:
    /// the note is consumed with no visual effect.  This is the intended default for
    /// <see cref="GameplayFeedbackSkinSet.miss"/> — a deliberate no-op, not an error.</para>
    ///
    /// <para>Consumed by <c>JudgementFeedbackRenderer</c> (planned, §5.11.2).</para>
    /// </summary>
    [System.Serializable]
    public struct JudgementFeedbackEntry
    {
        [Tooltip("When false, no visual effect is spawned for this judgement tier.\n\n" +
                 "A disabled Miss entry is the standard 'silent miss' design: the note is " +
                 "consumed with no feedback effect.  The renderer skips all work for this " +
                 "tier when disabled — it is not an error and logs no warnings.")]
        public bool enabled;

        [Tooltip("Shader template for this judgement effect.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex via MaterialPropertyBlock.\n" +
                 "  • Must support _Color via MaterialPropertyBlock (for tint × opacity).\n" +
                 "  • Unlit/Transparent is sufficient.\n\n" +
                 "Do NOT bake a texture into this material.  Ignored when enabled is false.")]
        public Material material;

        [Tooltip("(Optional) Texture for this judgement effect.\n\n" +
                 "Assigned to _MainTex via MaterialPropertyBlock at draw time.\n" +
                 "Leave null for a solid-color burst driven by tint alone.\n" +
                 "Ignored when enabled is false.")]
        public Texture2D texture;

        [Tooltip("Color tint for this judgement effect.\n\n" +
                 "Final shader color = tint × (alpha replaced by tint.a × opacity).\n" +
                 "Ignored when enabled is false.")]
        public Color tint;

        [Tooltip("Peak opacity of the effect at spawn time [0..1].\n\n" +
                 "The renderer fades from this value to 0 over fadeOutDuration.\n" +
                 "Default: 1.0\n" +
                 "Ignored when enabled is false.")]
        [Range(0f, 1f)]
        public float opacity;

        [Tooltip("Visual size of the effect in PlayfieldLocal units.\n\n" +
                 "Interpretation depends on the renderer — typically the radius of a ring " +
                 "burst or the half-extent of a quad around the hit point.\n" +
                 "Default: 0.05\n" +
                 "Ignored when enabled is false.")]
        [Min(0.001f)]
        public float sizeLocal;

        [Tooltip("Local Z offset above the arena surface to place this effect.\n\n" +
                 "Prevents Z-fighting with ArenaSurfaceRenderer layers.\n" +
                 "Should be slightly above the lane-touch highlight offset (if both are active).\n" +
                 "Default: 0.004\n" +
                 "Ignored when enabled is false.")]
        [Min(0f)]
        public float surfaceOffsetLocal;

        [Tooltip("Total lifetime of the effect in seconds from spawn to removal.\n\n" +
                 "The effect is alive for this duration; the fade-out begins at " +
                 "(lifetime − fadeOutDuration) seconds after spawn.\n" +
                 "Default: 0.40\n" +
                 "Ignored when enabled is false.")]
        [Min(0.01f)]
        public float lifetime;

        [Tooltip("Duration of the fade-out portion within the effect lifetime, in seconds.\n\n" +
                 "Must be ≤ lifetime.  Clamped in OnValidate.\n" +
                 "0 = effect disappears instantly at the end of lifetime.\n" +
                 "Default: 0.20\n" +
                 "Ignored when enabled is false.")]
        [Min(0f)]
        public float fadeOutDuration;

        [Tooltip("Speed at which this effect drifts in the +Z (above-surface) direction during " +
                 "its lifetime, in PlayfieldLocal units per second.\n\n" +
                 "0 = stationary (no drift upward).\n" +
                 "A small value (e.g. 0.04–0.08) produces a subtle float-up that reads clearly\n" +
                 "without obscuring note geometry.\n" +
                 "Default: 0.05\n" +
                 "Ignored when enabled is false.")]
        [Min(0f)]
        public float driftSpeedLocal;
    }

    // -------------------------------------------------------------------------
    // GameplayFeedbackSkinSet — ScriptableObject
    // -------------------------------------------------------------------------

    /// <summary>
    /// Describes the production feedback visuals for lane touch and judgement results.
    ///
    /// <para>This asset controls <b>appearance only</b>.  It is separate from
    /// <see cref="NoteSkinSet"/> (note bodies) and <c>ArenaSurfaceSkinSet</c> (arena fill).</para>
    ///
    /// <para>Sections:</para>
    /// <list type="bullet">
    ///   <item><b>Lane Touch</b> — subtle highlight while a lane is being touched
    ///     (consumed by <c>LaneTouchFeedbackRenderer</c>, planned §5.11.1).</item>
    ///   <item><b>Judgement</b> — per-tier visual (Perfect / Great / Miss)
    ///     (consumed by <c>JudgementFeedbackRenderer</c>, planned §5.11.2).</item>
    /// </list>
    ///
    /// <para><c>miss.enabled = false</c> is the intended default: note consumed with
    /// no visual effect.  This is not an error state.</para>
    ///
    /// <para>Create via <b>Assets → Create → RhythmicFlow → Gameplay Feedback Skin Set</b>.
    /// Assign to the feedback renderer components in the Inspector.</para>
    /// </summary>
    [CreateAssetMenu(
        menuName = "RhythmicFlow/Gameplay Feedback Skin Set",
        fileName = "NewGameplayFeedbackSkinSet",
        order    = 14)]
    public sealed class GameplayFeedbackSkinSet : ScriptableObject
    {
        // -------------------------------------------------------------------
        // Lane Touch Feedback
        //
        // Subtle highlight shown while the player's finger is in contact with
        // a lane.  This is a presence indicator, not a scored event.  It is
        // separate from the judgement feedback which fires on note hit/miss.
        //
        // Consumed by: LaneTouchFeedbackRenderer (planned — §5.11.1).
        // -------------------------------------------------------------------

        [Header("Lane Touch Feedback")]
        [Tooltip("Visual config for the lane-touch highlight.\n\n" +
                 "Active while a player's finger is in contact with a lane.\n" +
                 "This is a presence indicator only — it is NOT tied to note scoring.\n\n" +
                 "Disable this section to remove lane-touch highlights entirely.\n" +
                 "Consumed by LaneTouchFeedbackRenderer (planned — spec §5.11.1).")]
        [SerializeField] public LaneTouchFeedback laneTouchFeedback = new LaneTouchFeedback
        {
            enabled           = true,
            material          = null,
            texture           = null,
            tint              = new Color(1f, 1f, 1f, 0.4f),
            opacity           = 1.0f,
            laneWidthScale    = 1.0f,
            radialExtentLocal  = 0.05f,
            fullLaneCoverage   = false,
            overlayHeightLocal = 0.02f,
            fadeInDuration    = 0.05f,
            fadeOutDuration   = 0.12f,
        };

        // -------------------------------------------------------------------
        // Judgement Feedback — Perfect+ (optional distinct visual, spec §4.3)
        //
        // Perfect+ is a sub-window inside Perfect for display/stats only; it
        // does NOT change the score in v0.  When useSeparatePerfectPlusVisual
        // is true, hits within PerfectPlusWindowMs use THIS entry instead of
        // the standard perfect entry.  When false, Perfect+ falls back to perfect.
        //
        // Consumed by: JudgementFeedbackRenderer (§5.11.2).
        // -------------------------------------------------------------------

        [Header("Judgement Feedback — Perfect+ (optional distinct visual)")]
        [Tooltip("When true, notes judged Perfect+ (within PerfectPlusWindowMs, spec §4.3) " +
                 "use the perfectPlus entry below instead of the standard perfect entry.\n\n" +
                 "When false, Perfect+ hits fall back to the perfect entry — the sub-window " +
                 "has no distinct visual.\n\n" +
                 "Perfect+ is display-only; no score change in v0.")]
        [SerializeField] public bool useSeparatePerfectPlusVisual = false;

        [Tooltip("Visual effect for a Perfect+ hit (within PerfectPlusWindowMs, spec §4.3).\n\n" +
                 "Only used when useSeparatePerfectPlusVisual is true.\n" +
                 "A brighter or larger version of the Perfect effect works well here.\n\n" +
                 "Consumed by JudgementFeedbackRenderer (spec §5.11.2).")]
        [SerializeField] public JudgementFeedbackEntry perfectPlus = new JudgementFeedbackEntry
        {
            enabled           = true,
            material          = null,
            texture           = null,
            tint              = new Color(1.00f, 1.00f, 1.00f, 1.0f), // bright white — distinct from Perfect gold
            opacity           = 1.0f,
            sizeLocal         = 0.07f,   // slightly larger than Perfect
            surfaceOffsetLocal = 0.004f,
            lifetime          = 0.55f,
            fadeOutDuration   = 0.28f,
            driftSpeedLocal   = 0.06f,
        };

        // -------------------------------------------------------------------
        // Judgement Feedback — Perfect
        //
        // Fired when a note is hit within the Perfect timing window.
        // (Also used for Perfect+ when useSeparatePerfectPlusVisual is false.)
        // -------------------------------------------------------------------

        [Header("Judgement Feedback — Perfect")]
        [Tooltip("Visual effect spawned at the judgement ring when a note is hit Perfect.\n\n" +
                 "Also used for Perfect+ when useSeparatePerfectPlusVisual is false.\n\n" +
                 "Consumed by JudgementFeedbackRenderer (spec §5.11.2).\n\n" +
                 "Set enabled = false to suppress Perfect feedback entirely.")]
        [SerializeField] public JudgementFeedbackEntry perfect = new JudgementFeedbackEntry
        {
            enabled           = true,
            material          = null,
            texture           = null,
            tint              = new Color(1.00f, 0.92f, 0.40f, 1.0f), // golden yellow
            opacity           = 1.0f,
            sizeLocal         = 0.06f,
            surfaceOffsetLocal = 0.004f,
            lifetime          = 0.50f,
            fadeOutDuration   = 0.25f,
            driftSpeedLocal   = 0.05f,
        };

        // -------------------------------------------------------------------
        // Judgement Feedback — Great
        //
        // Fired when a note is hit within the Great (but not Perfect) window.
        // -------------------------------------------------------------------

        [Header("Judgement Feedback — Great")]
        [Tooltip("Visual effect spawned at the judgement ring when a note is hit Great.\n\n" +
                 "Consumed by JudgementFeedbackRenderer (planned — spec §5.11.2).\n\n" +
                 "Set enabled = false to suppress Great feedback entirely.")]
        [SerializeField] public JudgementFeedbackEntry great = new JudgementFeedbackEntry
        {
            enabled           = true,
            material          = null,
            texture           = null,
            tint              = new Color(0.50f, 0.80f, 1.00f, 1.0f), // light blue
            opacity           = 1.0f,
            sizeLocal         = 0.05f,
            surfaceOffsetLocal = 0.004f,
            lifetime          = 0.35f,
            fadeOutDuration   = 0.20f,
            driftSpeedLocal   = 0.04f,
        };

        // -------------------------------------------------------------------
        // Judgement Feedback — Miss
        //
        // Fired when a note is missed (expired without being hit).
        //
        // DESIGN NOTE — no-op miss path:
        //   enabled = false (the default) means "consume the note with no visual
        //   effect".  This is an intentional no-op design, not an error state.
        //   JudgementFeedbackRenderer must check enabled before spawning and skip
        //   all work silently when false.  Do not log a warning for disabled miss.
        // -------------------------------------------------------------------

        [Header("Judgement Feedback — Miss")]
        [Tooltip("Visual effect spawned when a note is missed.\n\n" +
                 "IMPORTANT: enabled = false (the default) means 'consume the note with " +
                 "no visual effect'.  This is a valid no-op design choice — the note is " +
                 "silently consumed and nothing is drawn.  Do not treat this as an error.\n\n" +
                 "Set enabled = true and assign a material only if you want a visible " +
                 "miss indicator (e.g. a dim grey flash).\n\n" +
                 "Consumed by JudgementFeedbackRenderer (planned — spec §5.11.2).")]
        [SerializeField] public JudgementFeedbackEntry miss = new JudgementFeedbackEntry
        {
            enabled           = false,  // intentional no-op: miss is silent by default
            material          = null,
            texture           = null,
            tint              = new Color(0.55f, 0.55f, 0.55f, 0.7f), // dim grey
            opacity           = 0.7f,
            sizeLocal         = 0.04f,
            surfaceOffsetLocal = 0.004f,
            lifetime          = 0.25f,
            fadeOutDuration   = 0.15f,
            driftSpeedLocal   = 0.02f,
        };

        // -------------------------------------------------------------------
        // Judgement Effect — Stack vs Replace policy
        //
        // Controls how JudgementFeedbackRenderer handles overlapping effects
        // when a new judgement fires while effects are still active on a lane.
        // -------------------------------------------------------------------

        [Header("Judgement Effect — Spawn Policy")]
        [Tooltip("Controls what happens when a new judgement fires while an effect for " +
                 "the same lane is still active:\n\n" +
                 "  Stack   — new effects layer on top; multiple bursts can be visible\n" +
                 "            simultaneously for the same lane.  Good for dense passages.\n\n" +
                 "  Replace — the new effect immediately deactivates any prior active\n" +
                 "            effect on that lane before spawning.  Keeps the display\n" +
                 "            clean during rapid note streams.\n\n" +
                 "Default: Stack.\n" +
                 "Note: runtime policy switching mid-song is not supported in v0.")]
        [SerializeField] public JudgementStackPolicy stackPolicy = JudgementStackPolicy.Stack;

        // -------------------------------------------------------------------
        // Hold-specific feedback — RESERVED (deferred)
        //
        // Hold-specific feedback variants (e.g. hold-tick pulse, hold-release
        // flash, hold-break indicator) are not yet specified.
        //
        // When specified, add them here as additional [SerializeField] entries
        // or a new serializable struct.  Do not add fields until the renderer
        // spec is defined — see §5.11 / §5.12 for the planned expansion path.
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        // Runtime helpers — lane touch feedback
        // Allocation-free; read-only convenience; called per draw call.
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the effective tint <see cref="Color"/> for the lane-touch highlight
        /// with alpha computed as <c>laneTouchFeedback.tint.a × laneTouchFeedback.opacity</c>.
        ///
        /// <para>Pass the returned color directly to
        /// <c>MaterialPropertyBlock.SetColor("_Color", …)</c>.</para>
        /// </summary>
        public Color GetLaneTouchEffectiveTint()
        {
            Color c = laneTouchFeedback.tint;
            c.a *= laneTouchFeedback.opacity;
            return c;
        }

        // -------------------------------------------------------------------
        // Runtime helpers — judgement feedback
        // Allocation-free; read-only convenience; called per note hit/miss.
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the effective tint <see cref="Color"/> for a judgement entry
        /// with alpha computed as <c>entry.tint.a × entry.opacity</c>.
        ///
        /// <para>Pass the returned color directly to
        /// <c>MaterialPropertyBlock.SetColor("_Color", …)</c>.</para>
        /// </summary>
        public Color GetJudgementEffectiveTint(in JudgementFeedbackEntry entry)
        {
            Color c = entry.tint;
            c.a *= entry.opacity;
            return c;
        }

        /// <summary>
        /// Returns the feedback entry for a given judgement string key.
        ///
        /// <para>Accepted keys (case-sensitive): <c>"Perfect"</c>, <c>"Great"</c>,
        /// <c>"Miss"</c>.  Unknown keys return the miss entry (disabled by default).</para>
        ///
        /// <para>Does not handle Perfect+; use
        /// <see cref="GetJudgementEntryForTier"/> for tier + Perfect+ aware lookup.</para>
        ///
        /// <para>The returned entry's <see cref="JudgementFeedbackEntry.enabled"/> flag
        /// must be checked by the renderer before spawning any effect.  A disabled entry
        /// is always a valid no-op — not an error.</para>
        /// </summary>
        public JudgementFeedbackEntry GetJudgementEntry(string judgementKey)
        {
            return judgementKey switch
            {
                "Perfect" => perfect,
                "Great"   => great,
                "Miss"    => miss,
                _         => miss,   // unknown key → silent no-op (miss is disabled by default)
            };
        }

        /// <summary>
        /// Returns the correct feedback entry for a judgement tier, accounting for
        /// the optional Perfect+ visual override.
        ///
        /// <para>When <see cref="useSeparatePerfectPlusVisual"/> is <c>true</c> and
        /// <paramref name="tier"/> is <see cref="JudgementTier.Perfect"/> and
        /// <paramref name="isPerfectPlus"/> is <c>true</c>, returns
        /// <see cref="perfectPlus"/>.  Otherwise follows the standard tier mapping.</para>
        ///
        /// <para>Always check the returned entry's <see cref="JudgementFeedbackEntry.enabled"/>
        /// before spawning.  A disabled entry is a silent no-op, not an error.</para>
        /// </summary>
        /// <param name="tier">The judgement tier from <see cref="JudgementRecord.Tier"/>.</param>
        /// <param name="isPerfectPlus">
        /// <c>true</c> when the hit is within <c>PerfectPlusWindowMs</c>
        /// (spec §4.3, display-only; no score change).
        /// </param>
        public JudgementFeedbackEntry GetJudgementEntryForTier(JudgementTier tier, bool isPerfectPlus)
        {
            // Perfect+ override: only when the flag is set AND the tier is Perfect.
            if (tier == JudgementTier.Perfect && isPerfectPlus && useSeparatePerfectPlusVisual)
            {
                return perfectPlus;
            }

            return tier switch
            {
                JudgementTier.Perfect => perfect,
                JudgementTier.Great   => great,
                JudgementTier.Miss    => miss,
                _                     => miss, // unknown tier → silent no-op
            };
        }

        // -------------------------------------------------------------------
        // Validation
        // -------------------------------------------------------------------

        private void OnValidate()
        {
            ValidateLaneTouchFeedback(ref laneTouchFeedback);
            ValidateJudgementEntry(ref perfectPlus);
            ValidateJudgementEntry(ref perfect);
            ValidateJudgementEntry(ref great);
            ValidateJudgementEntry(ref miss);
        }

        private static void ValidateLaneTouchFeedback(ref LaneTouchFeedback f)
        {
            f.opacity            = Mathf.Clamp01(f.opacity);
            f.laneWidthScale     = Mathf.Max(0.1f, f.laneWidthScale);
            f.radialExtentLocal  = Mathf.Max(0.005f, f.radialExtentLocal);
            f.overlayHeightLocal = Mathf.Max(0f, f.overlayHeightLocal);
            f.fadeInDuration     = Mathf.Max(0f, f.fadeInDuration);
            f.fadeOutDuration    = Mathf.Max(0f, f.fadeOutDuration);
        }

        private static void ValidateJudgementEntry(ref JudgementFeedbackEntry e)
        {
            e.opacity            = Mathf.Clamp01(e.opacity);
            e.sizeLocal          = Mathf.Max(0.001f, e.sizeLocal);
            e.surfaceOffsetLocal = Mathf.Max(0f, e.surfaceOffsetLocal);
            e.lifetime           = Mathf.Max(0.01f, e.lifetime);
            // fadeOutDuration must not exceed lifetime.
            e.fadeOutDuration    = Mathf.Clamp(e.fadeOutDuration, 0f, e.lifetime);
            e.driftSpeedLocal    = Mathf.Max(0f, e.driftSpeedLocal);
        }
    }
}
