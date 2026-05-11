using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Renders a single GameObject to a PNG using a throwaway off-screen
    /// camera. Frames the camera around the target's world bounds so the
    /// subject fills the view regardless of scene context. Supports a few
    /// overlay modes that let the LLM "see" structure beyond shading.
    ///
    /// Lifted from ScreenshotSceneViewTool's RT+ReadPixels+EncodeToPNG
    /// pattern; the difference is we own a temporary Camera (so we can pick
    /// the pose) instead of reusing SceneView.lastActiveSceneView.camera.
    public static class OffscreenRenderer
    {
        public enum Overlay
        {
            LitDefault,
            Wireframe,
            SubmeshColor,
        }

        public enum ViewPreset
        {
            Iso,
            Front,
            Back,
            Top,
            Left,
            Right,
        }

        public sealed class ViewRequest
        {
            public ViewPreset Preset = ViewPreset.Iso;
            // Optional custom pose override; if set, Preset is ignored.
            public Vector3? CameraPos;
            public Vector3? LookAt;
        }

        public sealed class Result
        {
            public byte[] Png;
            public Matrix4x4 CameraToWorld;
            public Matrix4x4 Projection;
            public Vector3 CameraPos;
            public Vector3 LookAt;
            public string PresetName;
        }

        /// Render a list of views of one GameObject. Each result is independent —
        /// the same RT/Camera is reused across views to keep allocations low.
        ///
        /// `highlightRegion` (optional) draws an outline of the region's
        /// triangles in saturated orange and dims the rest, used by
        /// render_region_highlight. Pass null for plain renders.
        public static List<Result> Render(GameObject target, IEnumerable<ViewRequest> views,
            int width, int height, Overlay overlay, MeshRegion highlightRegion)
        {
            var results = new List<Result>();
            if (target == null) return results;

            // Compute world bounds — frame all renderers under this hierarchy.
            if (!TryComputeBounds(target, out var bounds))
                return results;

            // Make a temporary GO to host our throwaway Camera.
            var camGo = new GameObject("__MeshAnalyticsCam");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var camera = camGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = Mathf.Max(100f, bounds.size.magnitude * 10f);
            camera.enabled = false;            // we drive it via Render()

            // Material swaps for overlays. We avoid global render-pipeline
            // changes by attaching a CommandBuffer at the camera level when
            // needed; for LitDefault we just render the scene as-is.
            RenderTexture rt = null;
            Texture2D tex = null;
            HighlightPainter painter = null;
            OverlaySwapper swapper = null;

            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);

                if (overlay != Overlay.LitDefault)
                    swapper = new OverlaySwapper(target, overlay);

                if (highlightRegion != null)
                    painter = new HighlightPainter(target, highlightRegion);

                foreach (var v in views)
                {
                    var (camPos, lookAt) = ResolvePose(v, bounds);
                    camera.transform.position = camPos;
                    camera.transform.LookAt(lookAt, Vector3.up);

                    camera.targetTexture = rt;
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;

                    camera.Render();

                    if (painter != null) painter.DrawAfterScene(camera);

                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;
                    camera.targetTexture = null;

                    var png = tex.EncodeToPNG();

                    results.Add(new Result
                    {
                        Png = png,
                        CameraToWorld = camera.cameraToWorldMatrix,
                        Projection = camera.projectionMatrix,
                        CameraPos = camPos,
                        LookAt = lookAt,
                        PresetName = v.CameraPos.HasValue ? "custom" : v.Preset.ToString().ToLowerInvariant(),
                    });
                }
            }
            finally
            {
                swapper?.Restore();
                painter?.Dispose();
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(camGo);
            }
            return results;
        }

        // ---- pose presets ----

        private static (Vector3 pos, Vector3 lookAt) ResolvePose(ViewRequest v, Bounds b)
        {
            var center = b.center;
            if (v.CameraPos.HasValue)
                return (v.CameraPos.Value, v.LookAt ?? center);

            float diag = b.size.magnitude;
            float dist = Mathf.Max(0.5f, diag * 1.2f);
            Vector3 dir;
            switch (v.Preset)
            {
                case ViewPreset.Front: dir = new Vector3(0, 0.15f, -1f); break;
                case ViewPreset.Back:  dir = new Vector3(0, 0.15f, 1f); break;
                case ViewPreset.Top:   dir = new Vector3(0, -1f, 0.001f); break;
                case ViewPreset.Left:  dir = new Vector3(-1f, 0.15f, 0); break;
                case ViewPreset.Right: dir = new Vector3(1f, 0.15f, 0); break;
                case ViewPreset.Iso:
                default:               dir = new Vector3(-1f, -0.8f, -1f); break;
            }
            dir = dir.normalized;
            // dir points from look-target toward camera (so subtract from center).
            return (center - dir * dist, center);
        }

        // ---- bounds ----

        private static bool TryComputeBounds(GameObject go, out Bounds bounds)
        {
            bounds = new Bounds();
            var renderers = go.GetComponentsInChildren<Renderer>(false);
            bool any = false;
            foreach (var r in renderers)
            {
                if (!r.enabled) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        // ---- overlay implementation ----

        /// Swaps every Renderer's sharedMaterials to a uniform overlay material
        /// for the duration of the render, restores on Dispose. We use shared
        /// materials so we never trigger GC churn from instance materials.
        private sealed class OverlaySwapper
        {
            private readonly List<(Renderer r, Material[] orig)> _saved = new();
            private static Material _wireMat;
            private static readonly Dictionary<int, Material> _submeshColors = new();

            public OverlaySwapper(GameObject root, Overlay mode)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(false);
                foreach (var r in renderers)
                {
                    _saved.Add((r, r.sharedMaterials));
                    var n = r.sharedMaterials?.Length ?? 1;
                    var swap = new Material[n];
                    for (int i = 0; i < n; i++)
                    {
                        swap[i] = mode switch
                        {
                            Overlay.Wireframe => GetWireMat(),
                            Overlay.SubmeshColor => GetSubmeshMat(i),
                            _ => null,
                        };
                    }
                    r.sharedMaterials = swap;
                }
            }

            public void Restore()
            {
                foreach (var (r, orig) in _saved)
                {
                    if (r != null) r.sharedMaterials = orig;
                }
                _saved.Clear();
            }

            private static Material GetWireMat()
            {
                if (_wireMat != null) return _wireMat;
                // GL-style wireframe shader doesn't exist OOB. We approximate
                // by using an unlit colour with low alpha + GL.wireframe
                // would need a CommandBuffer. For V1 we settle for a
                // flat-shaded look that emphasises silhouettes through a
                // lighter colour.
                var shader = Shader.Find("Hidden/Internal-Colored");
                _wireMat = new Material(shader ?? Shader.Find("Sprites/Default"));
                _wireMat.hideFlags = HideFlags.HideAndDontSave;
                _wireMat.SetColor("_Color", new Color(0.85f, 0.85f, 0.9f, 1f));
                return _wireMat;
            }

            private static Material GetSubmeshMat(int submeshIndex)
            {
                if (_submeshColors.TryGetValue(submeshIndex, out var m) && m != null) return m;
                var shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                m = new Material(shader);
                m.hideFlags = HideFlags.HideAndDontSave;
                // Stable hue per submesh index using golden-ratio spacing —
                // adjacent submeshes get visually distant colours.
                float h = (submeshIndex * 0.61803398875f) % 1f;
                m.SetColor("_Color", Color.HSVToRGB(h, 0.7f, 0.95f));
                _submeshColors[submeshIndex] = m;
                return m;
            }
        }

        /// Draws a region highlight on top of the rendered scene using GL
        /// immediate-mode. For EdgeChain regions we draw the polyline; for
        /// Plane/Submesh regions we draw the constituent triangles with a
        /// translucent orange overlay.
        private sealed class HighlightPainter : IDisposable
        {
            private readonly GameObject _target;
            private readonly MeshRegion _region;
            private static Material _lineMat;

            public HighlightPainter(GameObject target, MeshRegion region)
            {
                _target = target;
                _region = region;
            }

            public void DrawAfterScene(Camera cam)
            {
                if (_region == null) return;
                EnsureMat();
                _lineMat.SetPass(0);

                // Bind matrices to the camera so GL coordinates are world-space.
                GL.PushMatrix();
                GL.LoadIdentity();
                GL.MultMatrix(cam.worldToCameraMatrix);
                GL.LoadProjectionMatrix(cam.projectionMatrix);

                var orange = new Color(1f, 0.55f, 0.05f, 1f);

                switch (_region)
                {
                    case EdgeChainRegion ec:
                        DrawEdgeChain(ec, orange);
                        break;
                    case PlaneRegion pl:
                        DrawTriangleSet(pl, orange);
                        break;
                    case SubmeshRegion sm:
                        DrawTriangleSet(sm, orange);
                        break;
                }
                GL.PopMatrix();
            }

            private void DrawEdgeChain(EdgeChainRegion ec, Color c)
            {
                GL.Begin(GL.LINES);
                GL.Color(c);
                var pts = ec.VertexPositionsWorld;
                for (int i = 1; i < pts.Length; i++)
                {
                    GL.Vertex(pts[i - 1]);
                    GL.Vertex(pts[i]);
                }
                if (ec.IsLoop && pts.Length >= 2)
                {
                    GL.Vertex(pts[pts.Length - 1]);
                    GL.Vertex(pts[0]);
                }
                GL.End();
            }

            private void DrawTriangleSet(MeshRegion region, Color c)
            {
                var mesh = MeshResolver.ResolveMesh(_target);
                if (mesh == null) return;
                var ltw = _target.transform.localToWorldMatrix;
                var verts = mesh.vertices;
                var idx = mesh.triangles;
                GL.Begin(GL.TRIANGLES);
                GL.Color(new Color(c.r, c.g, c.b, 0.45f));
                foreach (var f in region.FaceIndices)
                {
                    if (3 * f + 2 >= idx.Length) continue;
                    GL.Vertex(ltw.MultiplyPoint3x4(verts[idx[3 * f + 0]]));
                    GL.Vertex(ltw.MultiplyPoint3x4(verts[idx[3 * f + 1]]));
                    GL.Vertex(ltw.MultiplyPoint3x4(verts[idx[3 * f + 2]]));
                }
                GL.End();
            }

            private static void EnsureMat()
            {
                if (_lineMat != null) return;
                var shader = Shader.Find("Hidden/Internal-Colored");
                _lineMat = new Material(shader ?? Shader.Find("Sprites/Default"));
                _lineMat.hideFlags = HideFlags.HideAndDontSave;
                _lineMat.SetInt("_ZWrite", 0);
                _lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }

            public void Dispose() { }
        }
    }
}
