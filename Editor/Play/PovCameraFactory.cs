using System;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Creates a one-shot debug Camera at a specified POV, renders it via
    /// GameViewCapture, then destroys it. Hidden from hierarchy + scene save
    /// (`HideFlags.HideAndDontSave`) so the scene stays untouched.
    ///
    /// Volume blending: if `includePostFx` and a reference camera is provided
    /// (typically Camera.main), we copy `cullingMask` so the temp camera sees
    /// the same Volume layers — global Volumes apply naturally; local Volume
    /// Triggers still require the POV to be inside the trigger's bounds.
    public static class PovCameraFactory
    {
        public sealed class PovSpec
        {
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
            public bool HasLookAt;
            public Vector3 LookAt;
            public float FieldOfView = 60f;
            public bool IsOrthographic;
            public float OrthographicSize = 5f;
            public float NearClip = 0.1f;
            public float FarClip = 1000f;
            public bool IncludePostFx = true;
        }

        public sealed class PovCameraScope : IDisposable
        {
            public Camera Camera;
            private GameObject _go;

            public void Dispose()
            {
                if (_go != null)
                {
                    UnityEngine.Object.DestroyImmediate(_go);
                    _go = null;
                    Camera = null;
                }
            }

            internal PovCameraScope(GameObject go, Camera cam) { _go = go; Camera = cam; }
        }

        /// Builds the temp Camera. Caller must `using` the scope and call
        /// GameViewCapture.CaptureSync on the camera before Dispose.
        public static PovCameraScope CreateScope(PovSpec spec, Camera referenceForLayers)
        {
            var go = new GameObject("__SceneAnalyzer_PovCamera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            go.transform.position = spec.Position;
            if (spec.HasLookAt)
            {
                Vector3 dir = spec.LookAt - spec.Position;
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
                go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            else
            {
                go.transform.rotation = spec.Rotation;
            }

            var cam = go.AddComponent<Camera>();
            cam.enabled = false; // we drive Render() manually; no player-loop integration
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.orthographic = spec.IsOrthographic;
            cam.fieldOfView = spec.FieldOfView;
            cam.orthographicSize = spec.OrthographicSize;
            cam.nearClipPlane = spec.NearClip;
            cam.farClipPlane = spec.FarClip;

            if (spec.IncludePostFx && referenceForLayers != null)
            {
                cam.cullingMask = referenceForLayers.cullingMask;
                // Attempting URP additional-camera-data via reflection so we
                // inherit Volume layer / post-fx-enabled. Best-effort — failure
                // means built-in pipeline or different SRP, just skip.
                TryCopyUrpAdditionalCameraData(referenceForLayers, cam);
            }
            return new PovCameraScope(go, cam);
        }

        private static void TryCopyUrpAdditionalCameraData(Camera src, Camera dst)
        {
            try
            {
                // Type discovered at runtime so this asmdef doesn't reference URP.
                var addType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                if (addType == null) return;

                var srcAdd = src.GetComponent(addType);
                if (srcAdd == null) return;

                var dstAdd = dst.gameObject.GetComponent(addType);
                if (dstAdd == null)
                {
                    dstAdd = dst.gameObject.AddComponent(addType);
                }

                // Copy a small set of fields/properties that matter for capture
                // parity: renderPostProcessing, volumeLayerMask, antialiasing,
                // renderType (we always want Base on the temp cam).
                CopyMember(addType, "renderPostProcessing", srcAdd, dstAdd);
                CopyMember(addType, "volumeLayerMask", srcAdd, dstAdd);
                CopyMember(addType, "antialiasing", srcAdd, dstAdd);
                // renderType: force Base regardless of source; we render alone.
                var rtProp = addType.GetProperty("renderType");
                if (rtProp != null && rtProp.PropertyType.IsEnum)
                {
                    var baseValue = Enum.Parse(rtProp.PropertyType, "Base", ignoreCase: true);
                    rtProp.SetValue(dstAdd, baseValue);
                }
            }
            catch
            {
                // URP not present or API drift — silent fallback to plain Camera.
            }
        }

        private static void CopyMember(Type type, string name, object src, object dst)
        {
            try
            {
                var prop = type.GetProperty(name);
                if (prop != null && prop.CanRead && prop.CanWrite)
                {
                    prop.SetValue(dst, prop.GetValue(src));
                    return;
                }
                var field = type.GetField(name);
                if (field != null)
                {
                    field.SetValue(dst, field.GetValue(src));
                }
            }
            catch { /* tolerate per-member drift */ }
        }
    }
}
