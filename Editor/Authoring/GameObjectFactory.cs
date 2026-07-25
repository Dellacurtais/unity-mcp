using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class GameObjectFactory
    {
        public static OpResult CreateEmpty(JObject args)
        {
            args ??= new JObject();
            if (!SceneAuthoringSupport.TryResolveParent(args["parent"], out var parent, out var error))
                return OpResult.Fail(error);
            if (!PrimitiveFactory.TryOptionalVector(args, "position", Vector3.zero, out var position, out error)
                || !PrimitiveFactory.TryOptionalRotation(args, "rotation", out var rotation, out error))
                return OpResult.Fail(error);

            var name = ((string)args["name"] ?? "GameObject").Trim();
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("INVALID_ARG: name must not be blank");

            using (var mutation = new MutationScope("Create Empty"))
            {
                var gameObject = new GameObject(name);
                mutation.RegisterCreated(gameObject, "Create Empty");
                if (parent != null) mutation.SetParent(gameObject.transform, parent.transform, "Parent");
                gameObject.transform.localPosition = position;
                gameObject.transform.localRotation = rotation;
                mutation.Complete();
                return OpResult.Success(gameObject.GetInstanceID(), $"created empty '{name}'");
            }
        }
    }
}
