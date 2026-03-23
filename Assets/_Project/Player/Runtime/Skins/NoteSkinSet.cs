// NoteSkinSet.cs
// ScriptableObject data container for note skins: Tap / Catch / Flick (single-interaction
// body family) plus Hold ribbon skin data.
//
// ── Single-interaction note body family ──────────────────────────────────────
//
//  Tap, Catch, and Flick are collectively the "single-interaction note body
//  family": they share one material template, one set of body-sizing parameters,
//  one skin layout contract (fixed-edge + tiled-center UV), and one missed-tint.
//
//    shared parameters:  noteBodyMaterial, noteLaneWidthRatio,
//                        noteRadialHalfThicknessLocal, bodyLeft/RightEdge*,
//                        bodyCenterTileRatePerUnit, missedTintColor
//
//  Flick differs from Tap/Catch only in:
//    - direction-specific body texture selection  (GetFlickBodyTexture)
//    - arrow overlay material, textures, and placement  (flickArrow* fields)
//
//  Hold is NOT part of this family. The hold ribbon uses different geometry
//  (trapezoid ribbon vs curved cap) and has its own skin layout contract.
//  Do NOT reuse noteLaneWidthRatio or noteRadialHalfThicknessLocal for Hold.
//
// ── Hold ribbon skin contract ─────────────────────────────────────────────────
//
//  Hold bodies use the same fixed-edge + tiled-center layout as single-interaction
//  bodies, but with two independent tiling axes:
//    • holdCenterTileRatePerUnit  — tiling across the ribbon width  (U axis)
//    • holdLengthTileRatePerUnit  — tiling along the ribbon length  (V axis)
//
//  holdLaneWidthRatio controls ribbon angular width (migrated from HoldBodyRenderer).
//  holdFlipVertical flips the V axis (consistent with the single-interaction pattern).
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
    /// Data container for all note skins: Tap / Catch / Flick (single-interaction body family)
    /// and Hold ribbon.
    ///
    /// <para>Tap, Catch, and Flick form the <b>single-interaction note body family</b>:
    /// they share one material template, one body-sizing set, one skin layout contract,
    /// and one missed-tint color. Flick adds direction-specific body texture overrides
    /// and an arrow overlay on top of the same shared family base.</para>
    ///
    /// <para>Hold has its own skin layout contract under the <b>Hold Body</b> headers:
    /// <see cref="holdBodyTexture"/>, independent edge UV fractions, two tiling axes
    /// (width + length), <see cref="holdLaneWidthRatio"/>, and <see cref="holdFlipVertical"/>.
    /// Use <see cref="GetHoldBodyTexture"/> to resolve the texture with fallback.</para>
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
        // Single-interaction note body family — material template
        // Used by: Tap, Catch, Flick.  Not used by: Hold (Hold has its own).
        // -------------------------------------------------------------------

        [Header("Single-Interaction Body — Material Template")]
        [Tooltip("Shared shader template for all Tap / Catch / Flick note bodies.\n\n" +
                 "Requirements:\n" +
                 "  • Must support _MainTex (the body texture) via MaterialPropertyBlock.\n" +
                 "  • Must support _Color via MaterialPropertyBlock (for missed-note tinting).\n" +
                 "  • A simple Unlit/Transparent shader is sufficient for v0.\n\n" +
                 "Do NOT bake a texture into this material — textures are assigned at draw time " +
                 "per note type via MaterialPropertyBlock._MainTex. One material asset is shared " +
                 "across Tap, Catch, and Flick.\n\n" +
                 "Hold uses its own material and will be migrated separately.")]
        [SerializeField] public Material noteBodyMaterial;

        // -------------------------------------------------------------------
        // Single-interaction note body family — per-type body textures
        // -------------------------------------------------------------------

        [Header("Single-Interaction Body — Per-Type Textures")]
        [Tooltip("Body texture for Tap notes.\n" +
                 "Assigned to _MainTex at draw time via MaterialPropertyBlock.\n" +
                 "Texture should use the fixed-edge + tiled-center layout (see bodyLeftEdgeU / bodyRightEdgeU).")]
        [SerializeField] public Texture2D tapBodyTexture;

        [Tooltip("Body texture for Catch notes.")]
        [SerializeField] public Texture2D catchBodyTexture;

        [Tooltip("Body texture for Flick notes (base body only; arrow overlay is separate).\n" +
                 "Direction-specific overrides in the section below take precedence when set.")]
        [SerializeField] public Texture2D flickBodyTexture;

        [Tooltip("(Optional) Fallback body texture used when a type-specific texture is null.\n" +
                 "Renderers log a one-time warning when falling back to this texture.\n" +
                 "Leave null to use a solid-color placeholder until textures are authored.")]
        [SerializeField] public Texture2D fallbackBodyTexture;

        // -------------------------------------------------------------------
        // Per-direction Flick body textures (Flick only — overrides flickBodyTexture)
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
        // Single-interaction note body family — texture orientation
        // -------------------------------------------------------------------

        [Header("Single-Interaction Body — Orientation")]
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
                 "Same convention as flipTapBodyVertical, applied to Flick only.")]
        [SerializeField] public bool flipFlickBodyVertical = false;

        // -------------------------------------------------------------------
        // Single-interaction note body family — skin layout (fixed-edge + tiled-center)
        // -------------------------------------------------------------------

        [Header("Single-Interaction Body — Skin Layout")]
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
        // Single-interaction note body family — sizing & state
        // Used by: Tap, Catch, Flick.
        //
        // Hold is intentionally excluded. The hold ribbon's angular width is
        // controlled by HoldBodyRenderer.holdLaneWidthRatio and will be
        // migrated to its own NoteSkinSet field when Hold skin is implemented.
        // The hold ribbon has no "radial half-thickness" concept — its radial
        // extent is determined by hold duration and approach speed.
        // -------------------------------------------------------------------

        [Header("Single-Interaction Body — Sizing & State")]
        [Tooltip("Note head width as a fraction of the lane angular span at the note's radius.\n" +
                 "1.0 = fills the full lane; 0.9 = 90% of the lane (default).\n" +
                 "Applied as noteHalfAngleDeg = laneHalfWidthDeg × noteLaneWidthRatio.\n\n" +
                 "Shared by Tap, Catch, and Flick (the single-interaction note body family).\n\n" +
                 "Hold ribbon width is separate: see HoldBodyRenderer.holdLaneWidthRatio.\n" +
                 "It will be migrated to a NoteSkinSet field when Hold skin is implemented.")]
        [Range(0.1f, 1f)]
        [SerializeField] public float noteLaneWidthRatio = 0.9f;

        [Tooltip("Radial half-thickness of the note head in PlayfieldLocal units.\n" +
                 "The note band spans [approachRadius − half, approachRadius + half].\n\n" +
                 "Shared by Tap, Catch, and Flick (the single-interaction note body family).\n\n" +
                 "Hold has no equivalent — its radial extent is determined by hold duration\n" +
                 "and approach speed, not a fixed half-thickness.\n" +
                 "Default: 0.022")]
        [Min(0.001f)]
        [SerializeField] public float noteRadialHalfThicknessLocal = 0.022f;

        [Tooltip("_Color tint applied via MaterialPropertyBlock to missed notes " +
                 "(State == Missed, visible until timeToHit < −greatWindowMs).\n\n" +
                 "Shared by Tap, Catch, and Flick (the single-interaction note body family).\n" +
                 "Default: (0.4, 0.4, 0.4, 0.55) — dim translucent grey.")]
        [SerializeField] public Color missedTintColor = new Color(0.4f, 0.4f, 0.4f, 0.55f);

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
        // Flick arrow overlay — size & placement
        // (Flick only — does not affect Tap/Catch body rendering)
        // -------------------------------------------------------------------

        [Header("Flick Arrow — Size & Placement")]
        [Tooltip("(Legacy) Uniform arrow size in PlayfieldLocal units.\n\n" +
                 "This field is kept for backward-compatibility. When arrowWidthLocal or " +
                 "arrowHeightLocal are set to a value greater than 0, they take precedence " +
                 "and this field is ignored for that axis.\n\n" +
                 "Prefer arrowWidthLocal / arrowHeightLocal for new authoring.\n" +
                 "Arrow size does NOT scale with lane width — arrows are readability elements.\n" +
                 "Default: 0.08")]
        [Min(0.001f)]
        [SerializeField] public float arrowSizeLocal = 0.08f;

        [Tooltip("Arrow overlay width (tangential extent) in PlayfieldLocal units.\n\n" +
                 "Controls how wide the arrow quad is across the lane (left-right when facing " +
                 "the arrow). Allows width and height to be tuned independently without editing the PNG.\n\n" +
                 "Set to 0 to fall back to the legacy arrowSizeLocal value for this axis.\n" +
                 "Arrow size does NOT scale with lane width — arrows are readability elements.\n" +
                 "Default: 0 (uses arrowSizeLocal)")]
        [Min(0f)]
        [SerializeField] public float arrowWidthLocal = 0f;

        [Tooltip("Arrow overlay height (radial extent) in PlayfieldLocal units.\n\n" +
                 "Controls how tall the arrow quad is along the gesture direction (tip-to-tail).\n" +
                 "Allows width and height to be tuned independently without editing the PNG.\n\n" +
                 "Set to 0 to fall back to the legacy arrowSizeLocal value for this axis.\n" +
                 "Arrow size does NOT scale with lane width — arrows are readability elements.\n" +
                 "Default: 0 (uses arrowSizeLocal)")]
        [Min(0f)]
        [SerializeField] public float arrowHeightLocal = 0f;

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

        // -------------------------------------------------------------------
        // Hold ribbon skin — body texture
        // Hold is NOT part of the single-interaction family. It has its own
        // material (on HoldBodyRenderer), its own geometry, and its own
        // sizing fields below.
        // -------------------------------------------------------------------

        [Header("Hold Body — Texture")]
        [Tooltip("Body texture for Hold notes (the ribbon body, not the head or tail caps).\n\n" +
                 "Assigned to _MainTex via MaterialPropertyBlock at draw time.\n" +
                 "Use GetHoldBodyTexture() to resolve this with fallback to fallbackBodyTexture.\n\n" +
                 "Texture should be authored using the hold fixed-edge + tiled-center layout:\n" +
                 "  [ left border | ← tiled center → | right border ]  (U axis, across ribbon width)\n\n" +
                 "Leave null to fall back to fallbackBodyTexture during development.")]
        [SerializeField] public Texture2D holdBodyTexture;

        // -------------------------------------------------------------------
        // Hold ribbon skin — skin layout (fixed-edge + tiled-center, both axes)
        // -------------------------------------------------------------------

        [Header("Hold Body — Skin Layout")]
        [Tooltip("Left decorative border width as a normalized fraction of hold texture width [0..0.5].\n\n" +
                 "Same convention as bodyLeftEdgeU on the single-interaction family, but for the hold ribbon.\n" +
                 "The leftmost (holdLeftEdgeU × texture width) pixels are the fixed decorative border.\n\n" +
                 "Default: 0.1")]
        [Range(0f, 0.5f)]
        [SerializeField] public float holdLeftEdgeU = 0.1f;

        [Tooltip("Right decorative border width as a normalized fraction of hold texture width [0..0.5].\n\n" +
                 "Same convention as bodyRightEdgeU on the single-interaction family, but for the hold ribbon.\n\n" +
                 "Default: 0.1")]
        [Range(0f, 0.5f)]
        [SerializeField] public float holdRightEdgeU = 0.1f;

        [Tooltip("Physical width of the hold ribbon's left decorative border in PlayfieldLocal units.\n\n" +
                 "Same convention as bodyLeftEdgeLocalWidth, applied to the hold ribbon width axis.\n" +
                 "Default: 0.012")]
        [Min(0f)]
        [SerializeField] public float holdLeftEdgeLocalWidth = 0.012f;

        [Tooltip("Physical width of the hold ribbon's right decorative border in PlayfieldLocal units.\n\n" +
                 "Same convention as bodyRightEdgeLocalWidth, applied to the hold ribbon width axis.\n" +
                 "Default: 0.012")]
        [Min(0f)]
        [SerializeField] public float holdRightEdgeLocalWidth = 0.012f;

        [Tooltip("How many times the hold center UV region tiles per PlayfieldLocal unit of ribbon chord width.\n\n" +
                 "Controls tiling across the ribbon (U axis). Same convention as bodyCenterTileRatePerUnit,\n" +
                 "applied to hold ribbon width only.\n\n" +
                 "Default: 1.0")]
        [Min(0.01f)]
        [SerializeField] public float holdCenterTileRatePerUnit = 1.0f;

        [Tooltip("How many times the hold texture tiles per PlayfieldLocal unit of ribbon length.\n\n" +
                 "Controls tiling along the ribbon (V axis / radial direction). Independent of the\n" +
                 "width-tiling rate (holdCenterTileRatePerUnit).\n\n" +
                 "Higher values produce shorter, more frequently repeated texture tiles along the ribbon.\n" +
                 "Default: 1.0")]
        [Min(0.01f)]
        [SerializeField] public float holdLengthTileRatePerUnit = 1.0f;

        // -------------------------------------------------------------------
        // Hold ribbon skin — sizing & orientation
        // holdLaneWidthRatio mirrors the single-interaction noteLaneWidthRatio
        // pattern, but with a separate default (0.7) appropriate for hold ribbons.
        // -------------------------------------------------------------------

        [Header("Hold Body — Sizing & Orientation")]
        [Tooltip("Hold ribbon width as a fraction of the lane angular span at the ribbon's radius.\n\n" +
                 "1.0 = fills the full lane; 0.7 = 70% of the lane width (default).\n" +
                 "Applied as ribbonHalfAngleDeg = laneHalfWidthDeg × holdLaneWidthRatio.\n\n" +
                 "Separate from noteLaneWidthRatio (the single-interaction family field) —\n" +
                 "hold ribbons are typically narrower than tap/catch/flick note heads.\n" +
                 "Default: 0.7")]
        [Range(0.1f, 1f)]
        [SerializeField] public float holdLaneWidthRatio = 0.7f;

        [Tooltip("Controls the V direction of the hold ribbon body texture.\n\n" +
                 "false (default) — normal orientation: V=0 at the head end (note hit point),\n" +
                 "V increases toward the tail.\n\n" +
                 "true — flipped orientation: V=1 at the head end, V=0 at the tail.\n\n" +
                 "Use this to correct art orientation at runtime without needing a flipped PNG.\n" +
                 "Same convention as flipTapBodyVertical, applied to the hold ribbon only.")]
        [SerializeField] public bool holdFlipVertical = false;

        // -------------------------------------------------------------------
        // Runtime helpers — single-interaction body family (Tap/Catch/Flick)
        // Allocation-free; read-only convenience; called per draw call.
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

        // -------------------------------------------------------------------
        // Runtime helpers — skin layout computed properties
        // Used by NoteCapGeometryBuilder.FillCapUVs and the edge-aware vertex builder.
        // -------------------------------------------------------------------

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
        // Runtime helpers — Hold ribbon skin
        // Allocation-free; read-only convenience; called per draw call.
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the hold ribbon body texture, falling back to
        /// <see cref="fallbackBodyTexture"/> if <see cref="holdBodyTexture"/> is null.
        /// Returns null if both are null (renderer should use color-only mode).
        /// </summary>
        public Texture2D GetHoldBodyTexture()
        {
            return holdBodyTexture != null ? holdBodyTexture : fallbackBodyTexture;
        }

        /// <summary>
        /// UV start of the hold ribbon's center region: <c>holdLeftEdgeU</c>.
        /// The hold center UV range is [HoldCenterUStart .. HoldCenterUEnd].
        /// </summary>
        public float HoldCenterUStart => holdLeftEdgeU;

        /// <summary>
        /// UV end of the hold ribbon's center region: <c>1f − holdRightEdgeU</c>.
        /// The hold center UV range is [HoldCenterUStart .. HoldCenterUEnd].
        /// </summary>
        public float HoldCenterUEnd => 1f - holdRightEdgeU;

        /// <summary>
        /// Width of the hold ribbon's center UV region: <c>HoldCenterUEnd − HoldCenterUStart</c>.
        /// Always ≥ 0 after <see cref="OnValidate"/> clamping.
        /// </summary>
        public float HoldCenterUWidth => HoldCenterUEnd - HoldCenterUStart;

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

            // Single-interaction body sizing — must be positive.
            noteLaneWidthRatio           = Mathf.Clamp(noteLaneWidthRatio, 0.1f, 1f);
            noteRadialHalfThicknessLocal = Mathf.Max(0.001f, noteRadialHalfThicknessLocal);

            // Arrow sizing — arrowSizeLocal must be positive (legacy fallback base).
            // arrowWidthLocal / arrowHeightLocal of 0 means "use arrowSizeLocal as fallback" —
            // that is intentional, so only prevent negatives here.
            arrowSizeLocal        = Mathf.Max(0.001f, arrowSizeLocal);
            arrowWidthLocal       = Mathf.Max(0f, arrowWidthLocal);
            arrowHeightLocal      = Mathf.Max(0f, arrowHeightLocal);
            arrowSurfaceOffsetLocal = Mathf.Max(0f, arrowSurfaceOffsetLocal);

            // Hold ribbon skin layout — mirror the same clamping pattern as the single-interaction family.
            float holdSumEdgeU = holdLeftEdgeU + holdRightEdgeU;
            if (holdSumEdgeU > 1f)
            {
                float scale = 1f / holdSumEdgeU;
                holdLeftEdgeU  *= scale;
                holdRightEdgeU *= scale;
            }

            holdLeftEdgeLocalWidth  = Mathf.Max(0f, holdLeftEdgeLocalWidth);
            holdRightEdgeLocalWidth = Mathf.Max(0f, holdRightEdgeLocalWidth);

            holdCenterTileRatePerUnit = Mathf.Max(0.01f, holdCenterTileRatePerUnit);
            holdLengthTileRatePerUnit = Mathf.Max(0.01f, holdLengthTileRatePerUnit);

            // Hold ribbon sizing.
            holdLaneWidthRatio = Mathf.Clamp(holdLaneWidthRatio, 0.1f, 1f);
        }
    }
}
