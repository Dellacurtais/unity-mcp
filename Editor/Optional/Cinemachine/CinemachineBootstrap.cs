using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.Cinemachine
{
    [InitializeOnLoad]
    internal static class CinemachineBootstrap
    {
        private const string PackageId = "com.unity.cinemachine";
        private static readonly string[] CameraTypes = { "Unity.Cinemachine.CinemachineCamera" };
        private static readonly string[] BrainTypes = { "Unity.Cinemachine.CinemachineBrain" };
        private static readonly IReadOnlyDictionary<string, string[]> Types =
            new Dictionary<string, string[]>
            {
                ["camera"] = CameraTypes,
                ["brain"] = BrainTypes,
            };
        private static readonly IReadOnlyDictionary<string, string[]> CameraProperties =
            new Dictionary<string, string[]>
            {
                ["priority"] = new[] { "Priority.Value", "Priority.m_Value", "m_Priority.Value", "m_Priority.m_Value", "Priority", "m_Priority" },
                ["trackingTarget"] = new[] { "TrackingTarget", "m_TrackingTarget", "Follow", "m_Follow" },
                ["lookAtTarget"] = new[] { "LookAtTarget", "m_LookAtTarget", "LookAt", "m_LookAt" },
                ["standbyUpdate"] = new[] { "StandbyUpdate", "m_StandbyUpdate" },
                ["fieldOfView"] = new[] { "Lens.FieldOfView", "m_Lens.FieldOfView", "m_Lens.m_FieldOfView" },
                ["orthographicSize"] = new[] { "Lens.OrthographicSize", "m_Lens.OrthographicSize", "m_Lens.m_OrthographicSize" },
                ["nearClip"] = new[] { "Lens.NearClipPlane", "m_Lens.NearClipPlane", "m_Lens.m_NearClipPlane" },
                ["farClip"] = new[] { "Lens.FarClipPlane", "m_Lens.FarClipPlane", "m_Lens.m_FarClipPlane" },
                ["dutch"] = new[] { "Lens.Dutch", "m_Lens.Dutch", "m_Lens.m_Dutch" },
            };

        static CinemachineBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("cinemachine", PackageId,
                    new OptionalContextTool("get_cinemachine_context", "Reports Cinemachine 3 package and scene context.", PackageId, Types),
                    new OptionalListComponentsTool("list_cinemachine_cameras", "Lists Cinemachine cameras and brains with stable targets.", PackageId, CameraTypes, BrainTypes),
                    new OptionalCreateComponentTool("create_cinemachine_camera", "Create Cinemachine camera", CameraTypes),
                    new OptionalConfigureComponentTool("configure_cinemachine_camera", "Configure Cinemachine camera", CameraTypes, CameraProperties),
                    new OptionalValidateTool("validate_cinemachine_setup", "Validates Cinemachine types and scene components.", PackageId, Types));
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
