using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.XR
{
    public sealed class AddXrGrabInteractableTool : IMcpTool
    {
        public string Name => "add_xr_grab_interactable";
        public string Description => "Adds XRGrabInteractable with the Rigidbody and non-trigger Collider required for grabbing.";
        public JObject InputSchema => OptionalToolSupport.TargetSchema();
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var gameObject = OptionalToolSupport.ResolveGameObject(arguments, out var error);
            if (gameObject == null) return error;
            var interactableType = OptionalToolSupport.ResolveType(XrBootstrap.GrabTypes);
            if (interactableType == null || !typeof(Component).IsAssignableFrom(interactableType))
                return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", XrBootstrap.GrabTypes));

            Component interactable;
            Collider collider;
            Rigidbody rigidbody;
            using (var mutation = new MutationScope("Add XR Grab Interactable"))
            {
                rigidbody = gameObject.GetComponent<Rigidbody>();
                if (rigidbody == null) rigidbody = mutation.AddComponent<Rigidbody>(gameObject);
                else mutation.Touch(rigidbody);

                collider = gameObject.GetComponents<Collider>().FirstOrDefault(candidate => candidate != null && !candidate.isTrigger);
                if (collider == null)
                {
                    collider = mutation.AddComponent<BoxCollider>(gameObject);
                    collider.isTrigger = false;
                }
                else mutation.Touch(collider);

                interactable = gameObject.GetComponent(interactableType);
                if (interactable == null) interactable = mutation.AddComponent(gameObject, interactableType);
                else mutation.Touch(interactable);
                mutation.Complete();
            }

            return ToolResult.Ok("configured XR grab interactable prerequisites", new JObject
            {
                ["target"] = ToolTargetRef.Capture(interactable)?.ToJson(),
                ["rigidbody"] = ToolTargetRef.Capture(rigidbody)?.ToJson(),
                ["collider"] = ToolTargetRef.Capture(collider)?.ToJson(),
                ["component"] = OptionalToolSupport.ComponentJson(interactable),
            });
        }
    }

    public sealed class ValidateXrInteractionSetupTool : IMcpTool
    {
        public string Name => "validate_xr_interaction_setup";
        public string Description => "Validates XR Interaction Toolkit managers, interactors, interactables and grab prerequisites.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var issues = new JArray();
            var managerType = OptionalToolSupport.ResolveType(XrBootstrap.ManagerTypes);
            var grabType = OptionalToolSupport.ResolveType(XrBootstrap.GrabTypes);
            var rayType = OptionalToolSupport.ResolveType(XrBootstrap.RayTypes);
            var directType = OptionalToolSupport.ResolveType(XrBootstrap.DirectTypes);

            if (managerType == null || grabType == null)
                return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", "XR Interaction Toolkit types are unavailable in the loaded package version");

            var managers = OptionalToolSupport.FindSceneComponents(managerType);
            if (managers.Length == 0)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "XR_INTERACTION_MANAGER_MISSING" });
            else if (managers.Length > 1)
                issues.Add(new JObject { ["severity"] = "warning", ["code"] = "MULTIPLE_XR_INTERACTION_MANAGERS", ["count"] = managers.Length });

            var grabs = OptionalToolSupport.FindSceneComponents(grabType);
            foreach (var grab in grabs)
            {
                var gameObject = grab.gameObject;
                if (gameObject.GetComponent<Rigidbody>() == null)
                    issues.Add(Issue("XR_GRAB_RIGIDBODY_MISSING", grab));
                var colliders = gameObject.GetComponents<Collider>();
                if (colliders.Length == 0)
                    issues.Add(Issue("XR_GRAB_COLLIDER_MISSING", grab));
                else if (!colliders.Any(collider => collider != null && !collider.isTrigger))
                    issues.Add(Issue("XR_GRAB_NON_TRIGGER_COLLIDER_MISSING", grab));
                foreach (var meshCollider in colliders.OfType<MeshCollider>())
                    if (gameObject.GetComponent<Rigidbody>() != null && !meshCollider.convex)
                        issues.Add(Issue("XR_DYNAMIC_MESH_COLLIDER_NOT_CONVEX", meshCollider));
            }

            var interactorCount = (rayType != null ? OptionalToolSupport.FindSceneComponents(rayType).Length : 0)
                + (directType != null ? OptionalToolSupport.FindSceneComponents(directType).Length : 0);
            if (grabs.Length > 0 && interactorCount == 0)
                issues.Add(new JObject { ["severity"] = "warning", ["code"] = "XR_INTERACTOR_MISSING" });

            return ToolResult.Ok($"{issues.Count} XR Interaction issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["managerCount"] = managers.Length,
                ["grabInteractableCount"] = grabs.Length,
                ["interactorCount"] = interactorCount,
                ["issues"] = issues,
            });
        }

        private static JObject Issue(string code, Component component) => new JObject
        {
            ["severity"] = "error",
            ["code"] = code,
            ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
        };
    }
}
