// NoteSkinSet.cs
// ScriptableObject data container for Tap / Catch / Flick note body skins.
//
// ── Authoring workflow ────────────────────────────────────────────────────────
//
//  1. Import your note-body PNG textures into the project.
//  2. Create a NoteSkinSet via  Assets → Create → RhythmicFlow → Note Skin Set.
//  3. Assign textures to tapBodyTexture, catchBodyTexture, flickBodyTexture.
//  4. Assign noteBodyMaterial (a simple Unlit/Transparent material template).
//  5. For flick arrows: import arrow PNG textures; assign to flickArrowTexture*
//     fields. Assign flickArrowMaterial (a shared Unlit/Transparent template).
//  6. Assign this asset to the NoteSkinSet field on each renderer in the Inspector.
//
//  Do NOT bake textures into material assets. Materials are shared shader
//  templates; the runtime assigns per-type / per-direction textures via
//  MaterialPropertyBlock (_MainTex) per draw call. One material asset can be
//  shared across all note types and all arrow directions.
//
// ── Skin layout contract ─────────────────────────────────────────────────────
//
//  Note bodies use a fixed-edge + tiled-center horizontal layout:
//
//    [ left border | ← tiled center → | right border ]
//    ←  fixed UV  →←  tiles with width →←  fixed UV  →
//
//  The decorative border regions are UV-mapped to fixed fractions of the texture
//  (bodyLeftEdgeU, bodyRightEdgeU) and never distorted when lane width changes.
//  The center region tiles at bodyCenterTileRatePerUnit per PlayfieldLocal unit
//  of note chord width.
//
// ── Implementation-agnostic design ───────────────────────────────────────────
//
//  The data fields in this asset do NOT assume whether tiling is handled by:
//    a) CPU-driven per-frame UV assignment into mesh.uv (current v0 implementation)
//    b) A custom shader receiving edge fractions + tile rate as properties (future)
//
//  The authoring data (edge U fractions, tile rate) maps cleanly to both.
//  Do not change the field names or semantics when adding shader-side support —
//  the same asset must continue to work without re-authoring.
//
// ── Spec reference ───────────────────────────────────────────────────────────
//
//  Spec §5.7.a (NoteSkinSet table) / §5.7.3 (skin philosophy, implementation path).

using UnityEngine;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Identifies which note body skin slot to look up on a <see cref="NoteSkinSet"/>.
    /// Intentionally separate from the gameplay <c>NoteType</c> enum so that
    /// <c>NoteSkinSet</c> does not depend on the gameplay assembly and can be
    /// reused freely by the Chart Editor preview layer.
    /// </summary>
    public enum NoteBodySkinType
    {
        Tap   = 0,
        Catch = 1,
        Flick = 2,
    }

    /// <summary>
    /// Data container for Tap / Catch / Flick note body skins.
    ///
    /// <para>Authoring workflow: import PNGs → assign to this asset → result appears
    /// in-game. One material template is shared; per-type textures are the primary
    /// authoring artifacts.</para>
    ///
    /// <para>Create via <b>Assets → Create → RhythmicFlow → Note Skin Set</b>.
    /// Assign to the <c>noteSkinSet</c> field on each production note renderer.</para>
    /// </summary>
    [CreateAssetMenu(
        menuName = "RhythmicFlow/Note Skin Set",
        fileName = "NewNoteSkinSet",
        order    = 10)]
    public sealed class NoteSkinSet : ScriptableObject
    {
        // -------------------------------------------------------------------
        // Body rendering — shared material template + per-type textures
        // -------------------------------------------------------------------

        [Header("Body Material Template")]
        [Tooltip("Shared shader template for all Tap / Catch / Flick note bodies.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex (the body texture) via MaterialPropertyBlock.\n" +
                 "  • Must support _Color via MaterialPropertyBlock (for missed-note tinting).\n" +
                 "  • A simple Unlit/Transparent shader is sufficient for v0.\n\n" +
                 "Do NOT bake a texture into this material — textures are assigned at draw time " +
                 "per note type via MaterialPropertyBlock._MainTex. One material asset is shared " +
                 "across all three types.")]
        [SerializeField] public Material noteBodyMaterial;

        [Header("Per-Type Body Textures")]
        [Tooltip("Body texture for Tap notes.\n" +
                 "Assigned to _MainTex at draw time via MaterialPropertyBlock.\n" +
                 "Texture should use the fixed-edge + tiled-center layout (see bodyLeftEdgeU / bodyRightEdgeU).")]
        [SerializeField] public Texture2D tapBodyTexture;

        [Tooltip("Body texture for Catch notes.")]
        [SerializeField] public Texture2D catchBodyTexture;

        [Tooltip("Body texture for Flick notes (base body only; arrow overlay is separate).")]
        [SerializeField] public Texture2D flickBodyTexture;

        [Tooltip("(Optional) Fallback body texture used when a type-specific texture is null.\n" +
                 "Renderers log a one-time warning when falling back to this texture.\n" +
                 "Leave null to use a solid-color placeholder until textures are authored.")]
        [SerializeField] public Texture2D fallbackBodyTexture;

        // -------------------------------------------------------------------
        // Per-direction Flick body textures  (Flick only — overrides flickBodyTexture)
        // -------------------------------------------------------------------

        [Header("Per-Direction Flick Body Textures (Optional Overrides)")]
        [Tooltip("(Optional) Body texture for Flick notes with direction Up (radially inward).\n" +
                 "When set, overrides flickBodyTexture for Up-flicks.\n" +
                 "Fallback chain: flickBodyTextureUp → flickBodyTexture → fallbackBodyTexture.")]
        [SerializeField] public Texture2D flickBodyTextureUp;

        [Tooltip("(Optional) Body texture for Flick notes with direction Down (radially outward).\n" +
                 "When set, overrides flickBodyTexture for Down-flicks.\n" +
                 "Fallback chain: flickBodyTextureDown → flickBodyTextureUp → flickBodyTexture → fallbackBodyTexture.")]
        [SerializeField] public Texture2D flickBodyTextureDown;

        [Tooltip("(Optional) Body texture for Flick notes with direction Left (clockwise tangential).\n" +
                 "When set, overrides flickBodyTexture for Left-flicks.\n" +
                 "Fallback chain: flickBodyTextureLeft → flickBodyTexture → fallbackBodyTexture.")]
        [SerializeField] public Texture2D flickBodyTextureLeft;

        [Tooltip("(Optional) Body texture for Flick notes with direction Right (counter-clockwise tangential).\n" +
                 "When set, overrides flickBodyTexture for Right-flicks.\n" +
                 "Fallback chain: flickBodyTextureRight → flickBodyTextureLeft → flickBodyTexture → fallbackBodyTexture.")]
        [SerializeField] public Texture2D flickBodyTextureRight;

        // -------------------------------------------------------------------
        // Body orientation — per-type vertical flip
        // -------------------------------------------------------------------

        [Header("Body Orientation — Vertical Flip")]
        [Tooltip("Controls the V direction of the Tap body texture on the note mesh.\n\n" +
                 "false (default) — normal orientation: V=0 on the inner (tail) edge, " +
                 "V=1 on the outer (head/front) edge.\n\n" +
                 "true — flipped orientation: V=1 on the inner edge, V=0 on the outer edge.\n\n" +
                 "Use this to correct art orientation at runtime without needing a flipped PNG.")]
        [SerializeField] public bool flipTapBodyVertical = false;

        [Tooltip("Controls the V direction of the Catch body texture on the note mesh.\n\n" +
                 "false (default) — normal orientation.\n" +
                 "true — flip V: V=1 on inner edge, V=0 on outer edge.\n\n" +
                 "Same convention as flipTapBodyVertical, applied to Catch only.")]
        [SerializeField] public bool flipCatchBodyVertical = false;

        [Tooltip("Controls the V direction of the Flick body texture on the note mesh.\n\n" +
                 "false (default) — normal orientation.\n" +
                 "true — flip V: V=1 on inner edge, V=0 on outer edge.\n\n" +
                 "Same convention as flipTapBodyVertical, applied to Flick only.\n" +
                 "(Flick renderer skin integration is not yet active — field is reserved.)")]
        [SerializeField] public bool flipFlickBodyVertical = false;

        // -------------------------------------------------------------------
        // Skin layout — fixed decorative edges + tiled center
        // -------------------------------------------------------------------

        [Header("Skin Layout — Fixed Edges + Tiled Center")]
        [Tooltip("Left decorative border width as a normalized fraction of texture width [0..0.5].\n\n" +
                 "The leftmost (bodyLeftEdgeU × texture width) pixels are the fixed decorative border.\n" +
                 "These pixels are always mapped to the physical left edge of the note body " +
                 "and are never distorted by lane width changes.\n\n" +
                 "Example: 0.1 = left 10% of the texture is the fixed left border.\n" +
                 "Default: 0.1")]
        [Range(0f, 0.5f)]
        [SerializeField] public float bodyLeftEdgeU = 0.1f;

        [Tooltip("Right decorative border width as a normalized fraction of texture width [0..0.5].\n\n" +
                 "The rightmost (bodyRightEdgeU × texture width) pixels are the fixed decorative border.\n" +
                 "These pixels are always mapped to the physical right edge of the note body.\n\n" +
                 "Example: 0.1 = right 10% of the texture is the fixed right border.\n" +
                 "Default: 0.1")]
        [Range(0f, 0.5f)]
        [SerializeField] public float bodyRightEdgeU = 0.1f;

        [Tooltip("Physical width of the left decorative border in PlayfieldLocal units.\n\n" +
                 "This controls how wide the left border region appears on screen, independent of " +
                 "lane width. The border is this many PlayfieldLocal units wide at any note width.\n\n" +
                 "Used by the CPU UV builder to determine how many columns belong to the " +
                 "left-edge region vs the tiled center region.\n" +
                 "Default: 0.012")]
        [Min(0f)]
        [SerializeField] public float bodyLeftEdgeLocalWidth = 0.012f;

        [Tooltip("Physical width of the right decorative border in PlayfieldLocal units.\n\n" +
                 "Same convention as bodyLeftEdgeLocalWidth, applied to the right border.\n" +
                 "Default: 0.012")]
        [Min(0f)]
        [SerializeField] public float bodyRightEdgeLocalWidth = 0.012f;

        [Tooltip("How many times the center UV region tiles per PlayfieldLocal unit of note chord width.\n\n" +
                 "Higher values produce a finer / denser center pattern.\n" +
                 "The center region is the UV span between bodyLeftEdgeU and (1 − bodyRightEdgeU).\n\n" +
                 "Example: 1.0 = one full tile repetition per local unit of center width.\n" +
                 "Default: 1.0")]
        [Min(0.01f)]
        [SerializeField] public float bodyCenterTileRatePerUnit = 1.0f;

        // -------------------------------------------------------------------
        // Flick arrow overlay — shared material template + per-direction textures
        // (Flick only — separate pass from the body)
        // -------------------------------------------------------------------

        [Header("Flick Arrow Overlay (Flick only)")]
        [Tooltip("Shared shader template for all flick arrow overlays.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex (the arrow texture) via MaterialPropertyBlock.\n" +
                 "  • A simple Unlit/Transparent shader is sufficient.\n\n" +
                 "Do NOT bake a texture into this material — textures are assigned at draw time " +
                 "per note direction via MaterialPropertyBlock._MainTex. One material asset is shared " +
                 "across all four arrow directions.\n\n" +
                 "Leave null to disable all arrow overlays.")]
        [SerializeField] public Material flickArrowMaterial;

        [Tooltip("(Optional) Generic fallback arrow texture used when no direction-specific slot is set.\n" +
                 "All four directions fall back to this if their own slot is null.\n" +
                 "Leave null if you are using direction-specific textures exclusively.")]
        [SerializeField] public Texture2D flickArrowTexture;

        [Tooltip("Arrow texture for Up-direction flicks (radially inward, toward arena center).\n" +
                 "Assigned to _MainTex via MaterialPropertyBlock at draw time.\n" +
                 "Fallback chain: flickArrowTextureUp → flickArrowTexture.\n" +
                 "Arrow texture should be authored with the arrow graphic pointing toward V=1 (+Y in UV).")]
        [SerializeField] public Texture2D flickArrowTextureUp;

        [Tooltip("(Optional) Arrow texture for Down-direction flicks (radially outward).\n" +
                 "Fallback chain: flickArrowTextureDown → flickArrowTextureUp → flickArrowTexture.")]
        [SerializeField] public Texture2D flickArrowTextureDown;

        [Tooltip("Arrow texture for Left-direction flicks (clockwise tangential).\n" +
                 "Fallback chain: flickArrowTextureLeft → flickArrowTexture.")]
        [SerializeField] public Texture2D flickArrowTextureLeft;

        [Tooltip("(Optional) Arrow texture for Right-direction flicks (CCW tangential).\n" +
                 "Fallback chain: flickArrowTextureRight → flickArrowTextureLeft → flickArrowTexture.")]
        [SerializeField] public Texture2D flickArrowTextureRight;

        // -------------------------------------------------------------------
        // Geometry and state parameters
        // -------------------------------------------------------------------

        [Header("Geometry Parameters")]
        [Tooltip("Note head width as a fraction of the lane angular span at the note's radius.\n" +
                 "1.0 = fills the full lane; 0.9 = 90% of the lane (default).\n" +
                 "Applied as noteHalfAngleDeg = laneHalfWidthDeg × noteLaneWidthRatio.")]
        [Range(0.1f, 1f)]
        [SerializeField] public float noteLaneWidthRatio = 0.9f;

        [Tooltip("Radial half-thickness of the note head in PlayfieldLocal units.\n" +
                 "The note band spans [approachRadius − half, approachRadius + half].\n" +
                 "Default: 0.022")]
        [Min(0.001f)]
        [SerializeField] public float noteRadialHalfThicknessLocal = 0.022f;

        [Tooltip("Constant arrow overlay size in PlayfieldLocal units.\n" +
                 "Arrow size does NOT scale with lane width — arrows are readability elements.\n" +
                 "Default: 0.08")]
        [Min(0.001f)]
        [SerializeField] public float arrowSizeLocal = 0.08f;

        [Tooltip("Local Z offset to lift the arrow quad above the note body surface.\n" +
                 "Prevents Z-fighting between the body mesh and the arrow billboard.\n" +
                 "Default: 0.003")]
        [Min(0f)]
        [SerializeField] public float arrowSurfaceOffsetLocal = 0.003f;

        [Tooltip("Arrow placement offset along the lane's radial direction, in PlayfieldLocal units.\n\n" +
                 "Radial = toward or away from the arena centre (the approach/judgement axis).\n" +
                 "  Positive → outward (toward judgement ring, same direction as the note approaches).\n" +
                 "  Negative → inward (toward arena centre).\n\n" +
                 "Applied after the note centre position, before surface Z offset.\n" +
                 "Default: 0 (centred on note).")]
        [SerializeField] public float arrowRadialOffsetLocal = 0f;

        [Tooltip("Arrow placement offset along the lane's tangential direction, in PlayfieldLocal units.\n\n" +
                 "Tangential = across the lane width, perpendicular to the radial direction.\n" +
                 "  Positive → counter-clockwise (same direction as the 'R' flick gesture).\n" +
                 "  Negative → clockwise (same direction as the 'L' flick gesture).\n\n" +
                 "Applied after the note centre position, before surface Z offset.\n" +
                 "Default: 0 (centred on note).")]
        [SerializeField] public float arrowTangentialOffsetLocal = 0f;

        [Header("State Colors")]
        [Tooltip("_Color tint applied via MaterialPropertyBlock to missed notes " +
                 "(State == Missed, visible until timeToHit < −greatWindowMs).\n" +
                 "Default: (0.4, 0.4, 0.4, 0.55) — dim translucent grey.")]
        [SerializeField] public Color missedTintColor = new Color(0.4f, 0.4f, 0.4f, 0.55f);

        // -------------------------------------------------------------------
        // Runtime helpers  (read-only convenience; no allocation)
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the body texture for the given skin type, falling back to
        /// <see cref="fallbackBodyTexture"/> if the type-specific slot is null.
        /// Returns null if both are null (renderer should fall back to color-only mode).
        /// </summary>
        public Texture2D GetBodyTexture(NoteBodySkinType bodySkinType)
        {
            Texture2D tex = bodySkinType switch
            {
                NoteBodySkinType.Tap   => tapBodyTexture,
                NoteBodySkinType.Catch => catchBodyTexture,
                NoteBodySkinType.Flick => flickBodyTexture,
                _                      => null,
            };
            return tex != null ? tex : fallbackBodyTexture;
        }

        /// <summary>
        /// Returns the body texture for a Flick note with the given direction string ("U"/"D"/"L"/"R"),
        /// applying direction-specific overrides before falling back to the generic flick texture
        /// and then to <see cref="fallbackBodyTexture"/>.
        ///
        /// <para>Fallback chains:</para>
        /// <list type="bullet">
        ///   <item><b>"U"</b> — flickBodyTextureUp → flickBodyTexture → fallbackBodyTexture</item>
        ///   <item><b>"D"</b> — flickBodyTextureDown → flickBodyTextureUp → flickBodyTexture → fallbackBodyTexture</item>
        ///   <item><b>"L"</b> — flickBodyTextureLeft → flickBodyTexture → fallbackBodyTexture</item>
        ///   <item><b>"R"</b> — flickBodyTextureRight → flickBodyTextureLeft → flickBodyTexture → fallbackBodyTexture</item>
        ///   <item>Unknown direction — flickBodyTexture → fallbackBodyTexture</item>
        /// </list>
        /// </summary>
        public Texture2D GetFlickBodyTexture(string flickDirection)
        {
            // Resolve direction-specific first candidate(s), then fall through to generic slots.
            Texture2D candidate = flickDirection switch
            {
                "U" => flickBodyTextureUp,
                "D" => flickBodyTextureDown != null ? flickBodyTextureDown : flickBodyTextureUp,
                "L" => flickBodyTextureLeft,
                "R" => flickBodyTextureRight != null ? flickBodyTextureRight : flickBodyTextureLeft,
                _   => null,
            };

            // Walk fallback chain: direction candidate → generic flick → fallback.
            if (candidate != null) return candidate;
            if (flickBodyTexture != null) return flickBodyTexture;
            return fallbackBodyTexture;
        }

        /// <summary>
        /// Returns the arrow texture for a Flick note with the given direction string
        /// ("U"/"D"/"L"/"R"), applying direction-specific overrides before falling back
        /// to <see cref="flickArrowTexture"/> (the generic fallback slot).
        ///
        /// <para>Fallback chains:</para>
        /// <list type="bullet">
        ///   <item><b>"U"</b> — flickArrowTextureUp → flickArrowTexture</item>
        ///   <item><b>"D"</b> — flickArrowTextureDown → flickArrowTextureUp → flickArrowTexture</item>
        ///   <item><b>"L"</b> — flickArrowTextureLeft → flickArrowTexture</item>
        ///   <item><b>"R"</b> — flickArrowTextureRight → flickArrowTextureLeft → flickArrowTexture</item>
        ///   <item>Unknown / undirected — flickArrowTexture</item>
        /// </list>
        ///
        /// Returns null when all slots (including <see cref="flickArrowTexture"/>) are unassigned.
        /// The renderer will silently skip the arrow overlay in that case.
        /// </summary>
        public Texture2D GetFlickArrowTexture(string flickDirection)
        {
            // Resolve the direction-specific candidate (with one-hop family fallback for D and R).
            Texture2D candidate = flickDirection switch
            {
                "U" => flickArrowTextureUp,
                "D" => flickArrowTextureDown != null ? flickArrowTextureDown : flickArrowTextureUp,
                "L" => flickArrowTextureLeft,
                "R" => flickArrowTextureRight != null ? flickArrowTextureRight : flickArrowTextureLeft,
                _   => null,
            };

            // Walk to generic fallback: direction candidate → generic arrow texture.
            return candidate != null ? candidate : flickArrowTexture;
        }

        /// <summary>
        /// Returns true when a direction-specific arrow texture is explicitly assigned for the
        /// given direction (i.e., the slot is non-null and will be used directly, not resolved
        /// through the family fallback chain).
        ///
        /// <para>Used by the renderer to decide whether to treat the resolved texture as
        /// authored for that exact direction (no rotation needed) or as a re-used texture
        /// that should be rotated 180° to point the arrow in the correct direction.</para>
        ///
        /// <para>Only "D" and "R" can be in fallback-derived orientation; "U" and "L" are
        /// always baseline (they are the family roots for their respective axis pairs).</para>
        /// </summary>
        public bool IsFlickArrowTextureExact(string flickDirection)
        {
            return flickDirection switch
            {
                "U" => flickArrowTextureUp    != null,
                "D" => flickArrowTextureDown  != null,
                "L" => flickArrowTextureLeft  != null,
                "R" => flickArrowTextureRight != null,
                _   => false,
            };
        }

        /// <summary>
        /// Returns the UV start of the center region: <c>bodyLeftEdgeU</c>.
        /// The center UV range is [CenterUStart .. CenterUEnd].
        /// </summary>
        public float CenterUStart => bodyLeftEdgeU;

        /// <summary>
        /// Returns the UV end of the center region: <c>1f − bodyRightEdgeU</c>.
        /// The center UV range is [CenterUStart .. CenterUEnd].
        /// </summary>
        public float CenterUEnd => 1f - bodyRightEdgeU;

        /// <summary>
        /// Width of the center UV region: <c>CenterUEnd − CenterUStart</c>.
        /// Always ≥ 0 after <see cref="OnValidate"/> clamping.
        /// </summary>
        public float CenterUWidth => CenterUEnd - CenterUStart;

        // -------------------------------------------------------------------
        // Validation
        // -------------------------------------------------------------------

        private void OnValidate()
        {
            // Clamp edge U fractions so their sum never exceeds 1 (no center region inversion).
            // Each is already [0..0.5] via Range attribute, but guard against both being 0.5.
            float sumEdgeU = bodyLeftEdgeU + bodyRightEdgeU;
            if (sumEdgeU > 1f)
            {
                // Scale both down proportionally to fit.
                float scale = 1f / sumEdgeU;
                bodyLeftEdgeU  *= scale;
                bodyRightEdgeU *= scale;
            }

            // Physical edge widths must not be negative (Min(0) attribute covers this,
            // but re-clamp defensively in case of programmatic assignment).
            bodyLeftEdgeLocalWidth  = Mathf.Max(0f, bodyLeftEdgeLocalWidth);
            bodyRightEdgeLocalWidth = Mathf.Max(0f, bodyRightEdgeLocalWidth);

            // Tile rate must be positive to avoid division-by-zero in UV builders.
            bodyCenterTileRatePerUnit = Mathf.Max(0.01f, bodyCenterTileRatePerUnit);

            // Geometry params must be positive.
            noteLaneWidthRatio            = Mathf.Clamp(noteLaneWidthRatio, 0.1f, 1f);
            noteRadialHalfThicknessLocal  = Mathf.Max(0.001f, noteRadialHalfThicknessLocal);
            arrowSizeLocal                = Mathf.Max(0.001f, arrowSizeLocal);
            arrowSurfaceOffsetLocal       = Mathf.Max(0f, arrowSurfaceOffsetLocal);
        }
    }
}
