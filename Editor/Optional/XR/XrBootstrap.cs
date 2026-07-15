using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.XR
{
    [InitializeOnLoad]
    internal static class XrBootstrap
    {
        private const string PackageId = "com.unity.xr.interaction.toolkit";
        internal static readonly string[] ManagerTypes = { "UnityEngine.XR.Interaction.Toolkit.XRInteractionManager" };
        internal static readonly string[] GrabTypes = { "UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable", "UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable" };
        internal static readonly string[] SimpleTypes = { "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable", "UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable" };
        internal static readonly string[] RayTypes = { "UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor", "UnityEngine.XR.Interaction.Toolkit.XRRayInteractor" };
        internal static readonly string[] DirectTypes = { "UnityEngine.XR.Interaction.Toolkit.Interactors.XRDirectInteractor", "UnityEngine.XR.Interaction.Toolkit.XRDirectInteractor" };
        private static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]>
        {
            ["interactionManager"] = ManagerTypes,
            ["grabInteractable"] = GrabTypes,
            ["simpleInteractable"] = SimpleTypes,
            ["rayInteractor"] = RayTypes,
            ["directInteractor"] = DirectTypes,
        };
        private static readonly IReadOnlyDictionary<string, string[]> GrabProperties = new Dictionary<string, string[]>
        {
            ["interactionLayers"] = new[] { "m_InteractionLayers" },
            ["selectMode"] = new[] { "m_SelectMode" },
            ["movementType"] = new[] { "m_MovementType" },
            ["throwOnDetach"] = new[] { "m_ThrowOnDetach" },
            ["trackPosition"] = new[] { "m_TrackPosition" },
            ["trackRotation"] = new[] { "m_TrackRotation" },
            ["smoothPosition"] = new[] { "m_SmoothPosition" },
            ["smoothRotation"] = new[] { "m_SmoothRotation" },
            ["retainTransformParent"] = new[] { "m_RetainTransformParent" },
        };

        static XrBootstrap() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("xr", PackageId,
                    new OptionalContextTool("get_xr_interaction_context", "Reports XR Interaction Toolkit package and scene types.", PackageId, Types),
                    new OptionalListComponentsTool("list_xr_interaction_components", "Lists XR managers, interactors and interactables.", PackageId, ManagerTypes, GrabTypes, SimpleTypes, RayTypes, DirectTypes),
                    new OptionalCreateComponentTool("create_xr_interaction_manager", "Create XR Interaction Manager", ManagerTypes),
                    new AddXrGrabInteractableTool(),
                    new OptionalConfigureComponentTool("configure_xr_grab_interactable", "Configure XR Grab Interactable", GrabTypes, GrabProperties),
                    new ValidateXrInteractionSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
