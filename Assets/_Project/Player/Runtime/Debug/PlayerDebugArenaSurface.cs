// PlayerDebugArenaSurface.cs
// DEBUG SCAFFOLDING — remove before shipping.
//
// Generates a solid ring-sector (annular arc band) mesh per arena so lanes appear
// to sit on a visible cone/frustum track in the Game view.
//
// Geometry is read from PlayerAppController debug getters and is generated once
// (v0 geometry is static).  Rebuilds automatically once the controller is ready.
//
// --- Vertex space ---
// PlayfieldRoot local XY defines the ring positions; local Z gives the frustum height.
// Conversion pipeline per vertex:
//   1. Compute (localX, localY, localZ) in PlayfieldRoot space.
//   2. worldPt  = pfRoot.TransformPoint(localX, localY, localZ)
//   3. meshPt   = go.transform.InverseTransformPoint(worldPt)
// Step 3 maps back to the mesh GO's local space so the mesh renders at the correct
// world position regardless of where the ArenaSurface GO is placed.
//
// --- Frustum profile ---
// Inner arc vertices are at localZ = frustumHeightInner (default 0.001 — just above
// the z=0 interaction plane to avoid z-fighting).
// Outer arc vertices are at localZ = frustumHeightOuter (default 0.15 — tilts the
// surface to create a cone/ramp effect).
// Hit-testing is always on the z=0 plane; these offsets are visual only.
//
// --- Triangulation ---
// Vertex layout: inner ring first (indices 0..N), outer ring second (N+1..2N+1).
// Each arc segment forms a quad from two CCW triangles (winding from +localZ):
//   Tri 1: inner[i] → outer[i]   → inner[i+1]
//   Tri 2: outer[i] → outer[i+1] → inner[i+1]
//
// Wiring:
//   1. Create empty GO "ArenaSurface" in PlayerBoot scene.
//   2. Add component PlayerDebugArenaSurface.
//   3. Assign PlayerAppController reference in the Inspector.
//   4. (Optional) assign a Material to materialOverride.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// DEBUG SCAFFOLDING: Generates a solid cone/frustum-sector mesh per arena band.
    /// Visual only — no effect on hit-testing or judgement.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Debug/PlayerDebugArenaSurface")]
    public class PlayerDebugArenaSurface : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("DEBUG SCAFFOLDING — remove before shipping")]

        [Tooltip("PlayerAppController in the scene.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Number of arc segments per ring-sector mesh (higher = smoother).")]
        [SerializeField] private int arcSegments = 64;

        [Header("Frustum Profile (DEBUG)")]

        [Tooltip("If true, inner/outer vertices are offset in PlayfieldRoot local Z to create a " +
                 "3D cone/frustum shape. The z=0 interaction plane is never modified.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z offset for inner-arc vertices. A small positive value avoids " +
                 "z-fighting with the z=0 interaction plane.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z offset for outer-arc vertices. Larger values create a steeper cone tilt.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        [Header("Material")]

        [Tooltip("Optional material for all arena surfaces. " +
                 "If null, a simple opaque runtime material is created automatically. " +
                 "Assign a transparent material here for a see-through surface.")]
        [SerializeField] private Material materialOverride;

        // -------------------------------------------------------------------
        // Internal state
        // -------------------------------------------------------------------

        // Meshes created at runtime — tracked so they can be explicitly Destroyed
        // in OnDestroy (sharedMesh is not destroyed with its MeshFilter's GO).
        private readonly List<Mesh> _meshes = new List<Mesh>();

        // Runtime material created when materialOverride is null.
        private Material _runtimeMat;

        // True after all arena meshes have been successfully generated.
        private bool _built;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            // Retry each LateUpdate until PlayerAppController geometry is ready.
            // (Script execution order is not guaranteed, so Start() may run before
            // the controller has finished its own Start().)
            if (!_built) { TryBuildMeshes(); }
        }

        private void OnDestroy()
        {
            // Child GOs (mesh objects) are destroyed automatically as children of this GO.
            // Meshes are separate assets and must be explicitly destroyed to prevent leaks.
            foreach (Mesh m in _meshes)
            {
                if (m != null) { Destroy(m); }
            }

            if (_runtimeMat != null) { Destroy(_runtimeMat); }
        }

        // -------------------------------------------------------------------
        // Mesh generation (one-shot)
        // -------------------------------------------------------------------

        private void TryBuildMeshes()
        {
            if (playerAppController == null) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas =
                playerAppController.DebugArenaGeometries;
            PlayfieldTransform pfT = playerAppController.DebugPlayfieldTransform;

            // Controller hasn't finished Start() yet.
            if (arenas == null || pfT == null) { return; }

            Material mat = materialOverride != null
                ? materialOverride
                : GetOrCreateRuntimeMaterial();

            Transform pfRoot = playerAppController.playfieldRoot;

            foreach (KeyValuePair<string, ArenaGeometry> kvp in arenas)
            {
                BuildArenaMesh(kvp.Key, kvp.Value, pfT, pfRoot, mat);
            }

            _built = true;
        }

        // Builds one ring-sector mesh and attaches it to a new child GameObject.
        private void BuildArenaMesh(
            string arenaId, ArenaGeometry geo,
            PlayfieldTransform pfT, Transform pfRoot,
            Material mat)
        {
            // Compute local-unit radii (spec §5.5).
            float outerLocal = pfT.NormRadiusToLocal(geo.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(geo.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Arena center in PlayfieldRoot local XY (spec §5.5).
            Vector2 center = pfT.NormalizedToLocal(new Vector2(geo.CenterXNorm, geo.CenterYNorm));

            // PlayfieldRoot local Z offsets for the frustum profile.
            // A tiny non-zero inner offset avoids z-fighting with the z=0 interaction plane.
            float zInner = useFrustumProfile ? frustumHeightInner : 0.001f;
            float zOuter = useFrustumProfile ? frustumHeightOuter : 0.001f;

            int N = Mathf.Max(1, arcSegments);

            // Vertex layout:
            //   indices  0 .. N   → inner arc  (s = 0, at innerLocal radius, z = zInner)
            //   indices N+1..2N+1 → outer arc  (s = 1, at outerLocal radius, z = zOuter)
            int        vertCount = (N + 1) * 2;
            Vector3[]  vertices  = new Vector3[vertCount];
            Vector2[]  uvs       = new Vector2[vertCount];
            int[]      triangles = new int[N * 6];   // N quads × 2 tris × 3 indices

            // Create the child GO now so InverseTransformPoint is available.
            // SetParent with worldPositionStays:false gives it the same world pose as
            // this component's GO (local pos/rot/scale = identity).
            var go = new GameObject($"ArenaSurface_{arenaId}");
            go.transform.SetParent(transform, worldPositionStays: false);

            // Populate vertices.
            for (int i = 0; i <= N; i++)
            {
                float t   = (float)i / N;
                float deg = geo.ArcStartDeg + t * geo.ArcSweepDeg;
                float rad = deg * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                // PlayfieldRoot local XY positions for inner and outer arc.
                Vector2 innerPt = center + dir * innerLocal;
                Vector2 outerPt = center + dir * outerLocal;

                // Convert: pfRoot local → world → mesh GO local.
                // This correctly handles any world placement of the ArenaSurface GO.
                Vector3 wInner = pfRoot.TransformPoint(innerPt.x, innerPt.y, zInner);
                Vector3 wOuter = pfRoot.TransformPoint(outerPt.x, outerPt.y, zOuter);

                vertices[i]         = go.transform.InverseTransformPoint(wInner);
                vertices[N + 1 + i] = go.transform.InverseTransformPoint(wOuter);

                // UVs: u = arc progress [0..1], v = 0 at inner edge, 1 at outer edge.
                uvs[i]         = new Vector2(t, 0f);
                uvs[N + 1 + i] = new Vector2(t, 1f);
            }

            // Triangulate as a strip.
            // For each segment i (0..N-1), one quad = two CCW triangles:
            //   Tri 1: inner[i]  → outer[i]   → inner[i+1]
            //   Tri 2: outer[i]  → outer[i+1] → inner[i+1]
            // This winding is CCW when viewed from PlayfieldRoot +localZ (front face).
            for (int i = 0; i < N; i++)
            {
                int ti  = i * 6;
                int iI  = i;
                int iI1 = i + 1;
                int oI  = N + 1 + i;
                int oI1 = N + 1 + i + 1;

                // Triangle 1
                triangles[ti + 0] = iI;
                triangles[ti + 1] = oI;
                triangles[ti + 2] = iI1;

                // Triangle 2
                triangles[ti + 3] = oI;
                triangles[ti + 4] = oI1;
                triangles[ti + 5] = iI1;
            }

            // Assemble the Mesh.
            var mesh = new Mesh();
            mesh.name      = $"ArenaSurface_{arenaId}";
            mesh.hideFlags = HideFlags.HideAndDontSave; // prevent accidental serialization
            mesh.vertices  = vertices;
            mesh.triangles = triangles;
            mesh.uv        = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // Attach rendering components to the child GO.
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh     = mesh;
            mr.sharedMaterial = mat;

            // Track the Mesh asset for manual cleanup in OnDestroy.
            _meshes.Add(mesh);
        }

        // -------------------------------------------------------------------
        // Runtime material
        // -------------------------------------------------------------------

        // Creates a simple opaque material when no materialOverride is assigned.
        // Tries URP Lit → Standard → Unlit/Color, using whichever shader is available.
        // Sets CullMode.Off so the surface is visible from both sides of the playfield.
        private Material GetOrCreateRuntimeMaterial()
        {
            if (_runtimeMat != null) { return _runtimeMat; }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Unlit/Color");

            _runtimeMat = new Material(shader);
            _runtimeMat.hideFlags = HideFlags.HideAndDontSave;

            // Neutral blue-gray, fully opaque.
            // To use a semi-transparent surface, assign a materialOverride with
            // transparency already configured.
            _runtimeMat.color = new Color(0.25f, 0.30f, 0.45f, 1.0f);

            // Disable back-face culling — the surface should be visible from both sides
            // of the playfield plane. This property name is shared by URP Lit and Standard.
            _runtimeMat.SetInt("_Cull", (int)CullMode.Off);

            return _runtimeMat;
        }
    }
}
