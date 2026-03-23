// ArenaSurfaceSkinSet.cs
// ScriptableObject data container describing how arena surfaces look.
//
// ── What this asset controls ──────────────────────────────────────────────────
//
//   ArenaSurfaceSkinSet controls arena surface APPEARANCE only:
//     – materials, textures, colors, opacity
//     – UV tiling and UV scroll animation
//     – per-layer enable toggles
//
//   It does NOT control:
//     – frustum geometry or cone heights  (see PlayfieldFrustumProfile)
//     – arena collider / input ownership  (see ArenaColliderProvider)
//     – arena outline / band strips       (see ArenaBandRenderer)
//     – note, hold, or arrow visuals      (see NoteSkinSet)
//     – touch or judgement feedback       (see GameplayFeedbackSkinSet)
//     – debug surface scaffolding         (see PlayerDebugArenaSurface)
//
// ── Layer structure ───────────────────────────────────────────────────────────
//
//   The surface supports up to three independent layers rendered in order:
//
//     Base      – the primary fill of the arena sector (required for any look)
//     Detail    – optional secondary pattern on top of the base (fine texture, grid…)
//     Accent    – optional highlight / glow rim or animated energy on top of detail
//
//   Each layer is independently enabled and has its own material template,
//   optional texture, tint color, opacity, UV scale, and UV scroll speed.
//   Layers are composited in the order above by the ArenaSurfaceRenderer.
//
// ── Material template pattern ─────────────────────────────────────────────────
//
//   Materials are shader templates — do NOT bake textures into them.
//   At draw time the renderer assigns each layer's texture to _MainTex and its
//   effective color (tint × opacity) to _Color via MaterialPropertyBlock.
//   One Unlit/Transparent material can be shared across all three layers if
//   they use the same shader.
//
// ── UV scroll ─────────────────────────────────────────────────────────────────
//
//   uvScrollSpeed is in normalized UV units per second (not pixels/sec).
//   A value of (0.1, 0) scrolls the texture 10% of its width per second.
//   Leave at (0, 0) for a static layer.
//
// ── Authoring workflow ────────────────────────────────────────────────────────
//
//   1. Create a skin asset via Assets → Create → RhythmicFlow → Arena Surface Skin Set.
//   2. Assign a material template to each enabled layer (Unlit/Transparent at minimum).
//   3. Optionally assign textures; leave null for a solid-color tint-only look.
//   4. Tune tint color, opacity, uvScale, and uvScrollSpeed per layer.
//   5. Assign this asset to the ArenaSurfaceRenderer in the Inspector.
//
// ── Spec reference ────────────────────────────────────────────────────────────
//
//   Spec §5.0 (production layering) / §5.8 (production playfield visual components).

using UnityEngine;

namespace RhythmicFlow.Player
{
    // -------------------------------------------------------------------------
    // ArenaSurfaceLayer — reusable serializable layer data
    //
    // Used for all three layers (base, detail, accent).  Keeping one struct for
    // all layers avoids duplicating Inspector fields and makes adding a fourth
    // layer trivial if needed later.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Data for one visual layer of an arena surface.
    /// Controls appearance only — not geometry, colliders, or frustum shape.
    /// </summary>
    [System.Serializable]
    public struct ArenaSurfaceLayer
    {
        [Tooltip("When false this layer is skipped entirely by the renderer.\n\n" +
                 "Disabling a layer costs nothing at runtime; the renderer omits the draw call.")]
        public bool enabled;

        [Tooltip("Shader template for this layer.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex (the layer texture) via MaterialPropertyBlock.\n" +
                 "  • Must support _Color via MaterialPropertyBlock (for tint × opacity).\n" +
                 "  • Unlit/Transparent is sufficient for most arena surface looks.\n\n" +
                 "Do NOT bake a texture into this material — texture is assigned at draw time " +
                 "via MaterialPropertyBlock._MainTex.  One material can be shared across all layers.")]
        public Material material;

        [Tooltip("(Optional) Texture assigned to _MainTex via MaterialPropertyBlock.\n\n" +
                 "Leave null for a solid-color fill driven by tint alone.\n" +
                 "The texture is tiled according to uvScale and scrolled by uvScrollSpeed.")]
        public Texture2D texture;

        [Tooltip("Color tint applied to this layer via MaterialPropertyBlock._Color.\n\n" +
                 "The final color sent to the shader is:  tint × (alpha replaced by layerOpacity × tint.a)\n\n" +
                 "Use the alpha channel here for an additional per-artist opacity tweak on top of\n" +
                 "the layerOpacity field, or simply set alpha to 1 and use layerOpacity alone.")]
        public Color tint;

        [Tooltip("Opacity multiplier for this layer [0 .. 1].\n\n" +
                 "Multiplied with tint.a before being sent to _Color.\n\n" +
                 "0 = fully transparent (layer effectively invisible).\n" +
                 "1 = full opacity (only tint.a governs transparency).\n" +
                 "Default: 1.0")]
        [Range(0f, 1f)]
        public float opacity;

        [Tooltip("UV tiling scale applied to this layer's texture.\n\n" +
                 "(1, 1) = texture fills the surface once (no repeat).\n" +
                 "(2, 2) = texture tiles twice in both U and V.\n" +
                 "Values < 1 zoom in; values > 1 tile more finely.\n\n" +
                 "Minimum clamped to (0.01, 0.01) in OnValidate to prevent divide-by-zero.\n" +
                 "Default: (1, 1)")]
        public Vector2 uvScale;

        [Tooltip("UV scroll speed in normalized texture units per second.\n\n" +
                 "(0.1, 0) = texture scrolls 10% of its width per second (horizontal drift).\n" +
                 "(0, 0.05) = texture scrolls 5% of its height per second (vertical drift).\n" +
                 "(0, 0) = static, no scroll.\n\n" +
                 "Scroll is cumulative over time; the renderer adds (uvScrollSpeed × deltaTime)\n" +
                 "to the UV offset each frame. Unconstrained — large values scroll quickly.")]
        public Vector2 uvScrollSpeed;
    }

    // -------------------------------------------------------------------------
    // ArenaSurfaceSkinSet — ScriptableObject
    // -------------------------------------------------------------------------

    /// <summary>
    /// Describes how arena surfaces look: materials, textures, colors, opacity,
    /// UV tiling, and optional UV scroll animation across up to three layers.
    ///
    /// <para>This asset controls <b>appearance only</b>. It does not own frustum
    /// geometry (<see cref="PlayfieldFrustumProfile"/>), arena colliders
    /// (<c>ArenaColliderProvider</c>), or debug surface scaffolding
    /// (<c>PlayerDebugArenaSurface</c>).</para>
    ///
    /// <para>Create via <b>Assets → Create → RhythmicFlow → Arena Surface Skin Set</b>.
    /// Assign to the <c>ArenaSurfaceRenderer</c> in the Inspector.</para>
    /// </summary>
    [CreateAssetMenu(
        menuName = "RhythmicFlow/Arena Surface Skin Set",
        fileName = "NewArenaSurfaceSkinSet",
        order    = 12)]
    public sealed class ArenaSurfaceSkinSet : ScriptableObject
    {
        // -------------------------------------------------------------------
        // Global
        // -------------------------------------------------------------------

        [Header("Global")]
        [Tooltip("Master opacity multiplier applied on top of all per-layer opacities.\n\n" +
                 "Use this to fade the entire arena surface in or out without changing\n" +
                 "individual layer settings.\n\n" +
                 "0 = fully transparent. 1 = layers render at their own opacity values.\n" +
                 "Default: 1.0")]
        [Range(0f, 1f)]
        [SerializeField] public float surfaceOpacityMultiplier = 1.0f;

        // -------------------------------------------------------------------
        // Base layer
        //
        // The primary fill of the arena sector.  Always drawn first.
        // For a simple opaque/translucent look this is typically the only
        // layer that needs to be enabled.
        // -------------------------------------------------------------------

        [Header("Base Layer")]
        [Tooltip("Primary fill layer of the arena surface.\n\n" +
                 "Drawn first (bottom-most).\n" +
                 "Enable this for any visible arena surface look.\n" +
                 "A simple solid-color look can be achieved with no texture and a tint color.")]
        [SerializeField] public ArenaSurfaceLayer baseLayer = new ArenaSurfaceLayer
        {
            enabled       = true,
            material      = null,
            texture       = null,
            tint          = new Color(0.3f, 0.35f, 0.45f, 0.7f),
            opacity       = 1.0f,
            uvScale       = Vector2.one,
            uvScrollSpeed = Vector2.zero,
        };

        // -------------------------------------------------------------------
        // Detail layer
        //
        // Optional secondary pattern on top of the base (e.g. a fine grid or
        // subtle noise texture).  Only drawn when enabled.
        // -------------------------------------------------------------------

        [Header("Detail Layer (Optional)")]
        [Tooltip("Secondary pattern layer drawn on top of the base layer.\n\n" +
                 "Useful for fine textures, grids, or subtle surface detail.\n" +
                 "Leave disabled (or set opacity to 0) if not needed.\n" +
                 "Drawn second, blended on top of the base layer.")]
        [SerializeField] public ArenaSurfaceLayer detailLayer = new ArenaSurfaceLayer
        {
            enabled       = false,
            material      = null,
            texture       = null,
            tint          = Color.white,
            opacity       = 0.4f,
            uvScale       = new Vector2(4f, 4f),
            uvScrollSpeed = Vector2.zero,
        };

        // -------------------------------------------------------------------
        // Accent layer
        //
        // Optional highlight, glow, or animated energy on top of the detail
        // layer.  Typically a brighter, more transparent layer with scroll.
        // -------------------------------------------------------------------

        [Header("Accent / Glow Layer (Optional)")]
        [Tooltip("Accent, glow, or animated energy layer drawn on top of the detail layer.\n\n" +
                 "Intended for rim highlights, energy flow animations, or glow effects.\n" +
                 "Set uvScrollSpeed to animate (e.g. (0.05, 0.1) for a slow drift).\n" +
                 "Leave disabled if not needed.\n" +
                 "Drawn third (top-most), blended on top of all other layers.")]
        [SerializeField] public ArenaSurfaceLayer accentLayer = new ArenaSurfaceLayer
        {
            enabled       = false,
            material      = null,
            texture       = null,
            tint          = new Color(0.5f, 0.7f, 1.0f, 0.3f),
            opacity       = 1.0f,
            uvScale       = Vector2.one,
            uvScrollSpeed = new Vector2(0f, 0.05f),
        };

        // -------------------------------------------------------------------
        // Runtime helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the effective opacity for <paramref name="layer"/> after applying
        /// <see cref="surfaceOpacityMultiplier"/>.
        ///
        /// <para>The renderer should multiply <c>layer.tint.a × layer.opacity</c> by
        /// this value to get the final alpha sent to the shader.</para>
        /// </summary>
        public float GetEffectiveOpacity(in ArenaSurfaceLayer layer)
        {
            return layer.opacity * surfaceOpacityMultiplier;
        }

        /// <summary>
        /// Returns the effective tint <see cref="Color"/> for <paramref name="layer"/> with
        /// alpha computed as <c>layer.tint.a × layer.opacity × surfaceOpacityMultiplier</c>.
        ///
        /// <para>Pass the returned color directly to <c>MaterialPropertyBlock.SetColor("_Color", …)</c>.</para>
        /// </summary>
        public Color GetEffectiveTint(in ArenaSurfaceLayer layer)
        {
            Color c = layer.tint;
            c.a *= layer.opacity * surfaceOpacityMultiplier;
            return c;
        }

        // -------------------------------------------------------------------
        // Validation
        // -------------------------------------------------------------------

        private void OnValidate()
        {
            surfaceOpacityMultiplier = Mathf.Clamp01(surfaceOpacityMultiplier);
            ValidateLayer(ref baseLayer);
            ValidateLayer(ref detailLayer);
            ValidateLayer(ref accentLayer);
        }

        private static void ValidateLayer(ref ArenaSurfaceLayer layer)
        {
            // Opacity must be in [0, 1].
            layer.opacity = Mathf.Clamp01(layer.opacity);

            // UV scale minimum: prevent divide-by-zero in UV computations.
            layer.uvScale.x = Mathf.Max(0.01f, layer.uvScale.x);
            layer.uvScale.y = Mathf.Max(0.01f, layer.uvScale.y);

            // UV scroll speed is unconstrained — large values just scroll faster.
        }
    }
}
