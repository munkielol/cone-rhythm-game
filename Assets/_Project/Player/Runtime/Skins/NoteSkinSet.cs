// NoteSkinSet.cs
// ScriptableObject data container for Tap / Catch / Flick note body skins.
//
// ── Authoring workflow ────────────────────────────────────────────────────────
//
//  1. Import your note-body PNG textures into the project.
//  2. Create a NoteSkinSet via  Assets → Create → RhythmicFlow → Note Skin Set.
//  3. Assign textures to tapBodyTexture, catchBodyTexture, flickBodyTexture.
//  4. Assign noteBodyMaterial (a simple Unlit/Transparent material template).
//  5. Assign this asset to the NoteSkinSet field on each renderer in the Inspector.
//
//  Do NOT bake textures into the material asset. The material is a shared shader
//  template; the runtime assigns the type-specific texture via MaterialPropertyBlock
//  (_MainTex) per draw call. This keeps one material asset shared across all types.
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
        // Flick arrow overlays  (Flick only — separate pass from the body)
        // -------------------------------------------------------------------

        [Header("Flick Arrow Materials (Flick only)")]
        [Tooltip("Material for the arrow overlay quad — Up direction (toward arena center).\n" +
                 "Used when FlickDirection == \"U\".")]
        [SerializeField] public Material flickArrowMaterialUp;

        [Tooltip("(Optional) Material for the arrow overlay quad — Down direction (away from arena center).\n" +
                 "Falls back to flickArrowMaterialUp rotated 180° when null.\n" +
                 "Used when FlickDirection == \"D\".")]
        [SerializeField] public Material flickArrowMaterialDown;

        [Tooltip("Material for the arrow overlay quad — Left direction (clockwise tangential).\n" +
                 "Used when FlickDirection == \"L\".")]
        [SerializeField] public Material flickArrowMaterialLeft;

        [Tooltip("(Optional) Material for the arrow overlay quad — Right direction (CCW tangential).\n" +
                 "Falls back to flickArrowMaterialLeft rotated 180° when null.\n" +
                 "Used when FlickDirection == \"R\".")]
        [SerializeField] public Material flickArrowMaterialRight;

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
        /// Returns the flick arrow material for the given direction string ("U"/"D"/"L"/"R"),
        /// falling back gracefully when optional slots are null.
        /// Returns null if no arrow material is configured for this direction.
        /// </summary>
        public Material GetFlickArrowMaterial(string flickDirection)
        {
            return flickDirection switch
            {
                "U" => flickArrowMaterialUp,
                "D" => flickArrowMaterialDown != null ? flickArrowMaterialDown : flickArrowMaterialUp,
                "L" => flickArrowMaterialLeft,
                "R" => flickArrowMaterialRight != null ? flickArrowMaterialRight : flickArrowMaterialLeft,
                _   => null,
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
