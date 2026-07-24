using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.PostFx.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tests
{
    public sealed class PostFxToolTests
    {
        private readonly List<GameObject> _gameObjects = new List<GameObject>();
        private readonly List<string> _assetPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            foreach (var gameObject in _gameObjects)
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            foreach (var path in _assetPaths)
                if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            _gameObjects.Clear();
            _assetPaths.Clear();
        }

        [Test]
        public void CreateVolume_LocalVolumeCreatesColliderAndReturnsProfileAsset()
        {
            var path = NewProfilePath();
            var tool = new CreateVolumeTool();
            var result = tool.Execute(new JObject
            {
                ["name"] = "Audit Local Volume",
                ["isGlobal"] = false,
                ["profilePath"] = path,
            });

            Assert.That(result.IsError, Is.False, result.Text);
            var gameObject = EditorUtility.InstanceIDToObject((int)result.Json["instanceId"]) as GameObject;
            Assert.That(gameObject, Is.Not.Null);
            _gameObjects.Add(gameObject);
            var collider = gameObject.GetComponent<BoxCollider>();
            Assert.That(collider, Is.Not.Null, "Local volumes need a collider to affect the scene.");
            Assert.That(collider.isTrigger, Is.True);
            Assert.That((string)result.Json["profilePath"], Is.EqualTo(path));
            Assert.That((string)result.Json["profileGuid"], Is.Not.Empty);
        }

        [Test]
        public void AddEffect_PersistsSubassetAndRejectsAmbiguousShortName()
        {
            var volume = CreateVolumeWithProfile(out var profile, out var path);
            AssertOk(PostEffectEditor.AddEffect(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                ["effect"] = typeof(AuditVolumeEffect).FullName,
            }));

            var effect = profile.components.Single(component => component is AuditVolumeEffect);
            Assert.That(AssetDatabase.GetAssetPath(effect), Is.EqualTo(path));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Has.Some.SameAs(effect));

            var ambiguous = PostEffectEditor.AddEffect(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                ["effect"] = "AuditSharedEffect",
            });
            Assert.That(ambiguous.Ok, Is.False);
            StringAssert.Contains("ambiguous", ambiguous.Error.ToLowerInvariant());
        }

        [Test]
        public void SetEffectProperty_UndoRestoresValueAndOverrideState()
        {
            var volume = CreateVolumeWithProfile(out var profile, out _);
            var effect = AddPersistentEffect<AuditVolumeEffect>(profile);
            Assert.That(effect.intensity.overrideState, Is.False);

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            AssertOk(PostEffectEditor.SetEffectProperty(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                ["effect"] = typeof(AuditVolumeEffect).FullName,
                ["property"] = "intensity",
                ["value"] = 2f,
            }));
            Undo.CollapseUndoOperations(group);
            Undo.FlushUndoRecordObjects();

            Assert.That(effect.intensity.value, Is.EqualTo(1f));
            Assert.That(effect.intensity.overrideState, Is.True);
            Undo.PerformUndo();
            Assert.That(effect.intensity.value, Is.EqualTo(0f));
            Assert.That(effect.intensity.overrideState, Is.False);
        }

        [Test]
        public void RemoveEffect_DeletesSubassetAndUndoRestoresIt()
        {
            var volume = CreateVolumeWithProfile(out var profile, out var path);
            var effect = AddPersistentEffect<AuditVolumeEffect>(profile);

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            AssertOk(PostEffectEditor.RemoveEffect(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                ["effect"] = typeof(AuditVolumeEffect).FullName,
            }));
            Undo.CollapseUndoOperations(group);

            Assert.That(profile.components, Has.None.InstanceOf<AuditVolumeEffect>());
            Assert.That(effect == null, Is.True, "Removed subassets must be destroyed, not orphaned.");
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Has.None.InstanceOf<AuditVolumeEffect>());

            Undo.PerformUndo();
            Assert.That(profile.components, Has.Some.InstanceOf<AuditVolumeEffect>());
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Has.Some.InstanceOf<AuditVolumeEffect>());
        }

        [Test]
        public void ApplyColorGrading_RequiresAtLeastOneValueBeforeResolvingThePipelineEffect()
        {
            var volume = CreateVolumeWithProfile(out var profile, out _);
            var empty = PostEffectEditor.ApplyColorGrading(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
            });

            Assert.That(empty.Ok, Is.False);
            Assert.That(profile.components, Is.Empty);
            StringAssert.Contains("at least one", empty.Error.ToLowerInvariant());
        }

        [Test]
        public void DiscoveryTools_ReturnStructuredEffectsAndParameterBounds()
        {
            var volume = CreateVolumeWithProfile(out var profile, out _);
            AddPersistentEffect<AuditVolumeEffect>(profile);

            var list = new ListPostEffectsTool().Execute(new JObject
            {
                ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
            });
            Assert.That(list.IsError, Is.False, list.Text);
            Assert.That((int)list.Json["count"], Is.EqualTo(1));
            Assert.That((string)list.Json["effects"][0]["type"], Is.EqualTo(typeof(AuditVolumeEffect).FullName));

            var describe = new DescribePostEffectTool().Execute(new JObject
            {
                ["effect"] = typeof(AuditVolumeEffect).FullName,
            });
            Assert.That(describe.IsError, Is.False, describe.Text);
            var intensity = describe.Json["parameters"].Single(item => (string)item["name"] == "intensity");
            Assert.That((float)intensity["minimum"], Is.EqualTo(0f));
            Assert.That((float)intensity["maximum"], Is.EqualTo(1f));
            Assert.That((bool)intensity["noInterp"], Is.False);
        }

        private Volume CreateVolumeWithProfile(out VolumeProfile profile, out string path)
        {
            path = NewProfilePath();
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            var gameObject = new GameObject("PostFxToolTestVolume");
            _gameObjects.Add(gameObject);
            var volume = gameObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            return volume;
        }

        private T AddPersistentEffect<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var effect = profile.Add<T>(false);
            AssetDatabase.AddObjectToAsset(effect, profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return effect;
        }

        private string NewProfilePath()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneAnalyzerPostFxToolTest.asset");
            _assetPaths.Add(path);
            return path;
        }

        private static void AssertOk(OpResult result)
        {
            Assert.That(result.Ok, Is.True, result.Error);
        }
    }
}

namespace UnityEngine.Rendering.Universal
{
    [Serializable]
    public sealed class AuditSharedEffect : VolumeComponent
    {
        public FloatParameter amount = new FloatParameter(0f);
    }
}

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public sealed class AuditSharedEffect : VolumeComponent
    {
        public FloatParameter amount = new FloatParameter(0f);
    }
}
