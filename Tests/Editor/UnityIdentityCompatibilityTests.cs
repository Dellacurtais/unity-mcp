using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class UnityIdentityCompatibilityTests
    {
        [Test]
        public void ObjectHandles_RoundTripJsonAndDistinguishComponents()
        {
            var first = new GameObject("identity-first");
            var second = new GameObject("identity-second");
            try
            {
                var handle = first.GetMcpInstanceId();
                var json = JObject.Parse(new JObject { ["instanceId"] = handle }.ToString());
                Assert.That(handle, Is.Not.Zero);
                Assert.That(first.GetMcpInstanceId(), Is.EqualTo(handle));
                Assert.That(second.GetMcpInstanceId(), Is.Not.EqualTo(handle));
                Assert.That(first.transform.GetMcpInstanceId(), Is.Not.EqualTo(handle));
                Assert.That(UnityObjectIds.Resolve((int)json["instanceId"]), Is.SameAs(first));
                Assert.That(UnityObjectIds.Resolve(first.transform.GetMcpInstanceId()), Is.SameAs(first.transform));
#if UNITY_6000_2_OR_NEWER
                Assert.That(UnityObjectIds.Resolve(handle).GetEntityId(), Is.EqualTo(first.GetEntityId()));
#endif
            }
            finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
        }

        [Test]
        public void DestroyedHandle_DoesNotResolveReplacement()
        {
            var original = new GameObject("identity-reused-name");
            var handle = original.GetMcpInstanceId();
            var target = ToolTargetRef.Capture(original);
            Object.DestroyImmediate(original);
            var replacement = new GameObject("identity-reused-name");
            try
            {
                Assert.That(replacement.GetMcpInstanceId(), Is.Not.EqualTo(handle));
                Assert.That(UnityObjectIds.Resolve(handle), Is.Null);
                Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
                Assert.That(error, Does.StartWith("TARGET_IDENTITY_STALE"));
                Assert.That(UnityObjectIds.Resolve(0), Is.Null);
                Assert.That(UnityObjectIds.Resolve(int.MaxValue), Is.Null);
            }
            finally { Object.DestroyImmediate(replacement); }
        }

        [Test]
        public void SerializedReferences_CompareNativeIdentityAndDetectEmptyValue()
        {
            var go = new GameObject("identity-reference");
            var filter = go.AddComponent<MeshFilter>();
            var mesh = new Mesh();
            var other = new Mesh();
            try
            {
                filter.sharedMesh = mesh;
                using (var serialized = new SerializedObject(filter))
                {
                    var property = serialized.FindProperty("m_Mesh");
                    Assert.That(UnityObjectIds.HasObjectReference(property), Is.True);
                    Assert.That(UnityObjectIds.References(property, mesh), Is.True);
                    Assert.That(UnityObjectIds.References(property, other), Is.False);
                    property.objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    Assert.That(UnityObjectIds.HasObjectReference(property), Is.False);
                }
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(mesh); Object.DestroyImmediate(other); }
        }

        [Test]
        public void SceneHandles_KeepPreviewScenesDistinctWithoutChangingActiveScene()
        {
            var previous = SceneManager.GetActiveScene();
            var first = EditorSceneManager.NewPreviewScene();
            var second = EditorSceneManager.NewPreviewScene();
            try
            {
                Assert.That(first.GetMcpSceneHandle(), Is.Not.Zero);
                Assert.That(first.GetMcpSceneHandle(), Is.Not.EqualTo(second.GetMcpSceneHandle()));
                var go = new GameObject("identity-scene-target");
                SceneManager.MoveGameObjectToScene(go, first);
                var target = ToolTargetRef.Capture(go);
                Assert.That(target.SceneHandle, Is.EqualTo(first.GetMcpSceneHandle()));
                Assert.That(ToolTargetResolver.Resolve(ToolTargetRef.FromJson(target.ToJson()), out _), Is.SameAs(go));
                target.SceneHandle = second.GetMcpSceneHandle();
                Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
                Assert.That(error, Does.StartWith("TARGET_SCENE_MISMATCH"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(second);
                EditorSceneManager.ClosePreviewScene(first);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
        }
    }
}
