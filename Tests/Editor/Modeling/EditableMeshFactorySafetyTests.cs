using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class EditableMeshFactorySafetyTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = AssetModelerAssetPaths.ValidateAndEnsureFolder("Assets/AssetModelerTests/FactorySafety_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null && !EditorUtility.IsPersistent(_objects[i])) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            AssetDatabase.DeleteAsset(_folder);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticAnimatedChild_CopyIncludesAnimationRootAndBindings(bool legacy)
        {
            var root = Keep(new GameObject("AnimatedRoot"));
            var selected = AddMesh(root, "Selected", TestMesh());
            var sibling = new GameObject("Sibling");
            sibling.transform.SetParent(root.transform, false);
            var clip = Keep(new AnimationClip { name = "MoveChild", legacy = legacy });
            clip.SetCurve("Selected", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 4));
            if (legacy)
            {
                var animation = root.AddComponent<Animation>();
                animation.AddClip(clip, "MoveChild");
                animation.clip = clip;
            }
            else
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(_folder + "/Static.controller");
                AssetDatabase.CreateAsset(clip, _folder + "/MoveChild.anim");
                controller.layers[0].stateMachine.AddState("MoveChild").motion = clip;
                root.AddComponent<Animator>().runtimeAnimatorController = controller;
            }

            // The legacy API receives a renderer child and must recover its animated model root.
            var result = EditableMeshFactory.Create(selected.gameObject, _folder);
            Keep(result.EditableRoot);
            Assert.That(result.EditableRoot, Is.Not.SameAs(result.EditableObject));
            Assert.That(result.EditableRoot.transform.childCount, Is.EqualTo(2));
            Assert.That(result.EditableRoot.transform.Find("Sibling"), Is.Not.Null);
            if (legacy) Assert.That(result.EditableRoot.GetComponent<Animation>(), Is.Not.Null);
            else Assert.That(result.EditableRoot.GetComponent<Animator>().runtimeAnimatorController,
                Is.SameAs(root.GetComponent<Animator>().runtimeAnimatorController));
            clip.SampleAnimation(result.EditableRoot, 0.5f);
            Assert.That(result.EditableObject.transform.localPosition.x, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(selected.transform.localPosition.x, Is.EqualTo(0f));
            Assert.That(result.EditableRenderer.transform.IsChildOf(result.EditableRoot.transform), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedMesh_OnlySelectedRenderersColliderFollowsEditedMesh(bool existingCopy)
        {
            var root = Keep(new GameObject("EditableRoot"));
            var mesh = TestMesh();
            var selected = AddMesh(root, "Selected", mesh);
            var other = AddMesh(root, "Other", mesh);
            var selectedCollider = selected.gameObject.AddComponent<MeshCollider>(); selectedCollider.sharedMesh = mesh;
            var otherCollider = other.gameObject.AddComponent<MeshCollider>(); otherCollider.sharedMesh = mesh;

            var result = existingCopy
                ? EditableMeshFactory.CreateForExistingCopy(root, selected, _folder)
                : EditableMeshFactory.CreateModelCopy(root, selected, _folder);
            if (!existingCopy) Keep(result.EditableRoot);
            var copiedOther = result.EditableRoot.transform.Find("Other");
            Assert.That(result.EditableRenderer.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(result.EditableMesh));
            Assert.That(copiedOther.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(mesh));
            Assert.That(copiedOther.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
            var vertices = result.EditableMesh.vertices;
            vertices[0] += Vector3.up * 3;
            result.EditableMesh.vertices = vertices;
            Assert.That(copiedOther.GetComponent<MeshCollider>().sharedMesh.vertices[0], Is.EqualTo(Vector3.zero));
            Assert.That(otherCollider.sharedMesh, Is.SameAs(mesh));
            if (!existingCopy) Assert.That(selectedCollider.sharedMesh, Is.SameAs(mesh));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AmbiguousOrphanCollider_FailsBeforeCreatingOutputOrChangingReferences(bool existingCopy)
        {
            var root = Keep(new GameObject("AmbiguousRoot"));
            var mesh = TestMesh();
            var selected = AddMesh(root, "Selected", mesh);
            AddMesh(root, "Other", mesh);
            var orphan = new GameObject("UnassignedCollision"); orphan.transform.SetParent(root.transform, false);
            var collider = orphan.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
            int assetsBefore = AssetDatabase.FindAssets("", new[] { _folder }).Length;
            string output = _folder + "/MustNotExist";

            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                if (existingCopy) EditableMeshFactory.CreateForExistingCopy(root, selected, output);
                else EditableMeshFactory.CreateModelCopy(root, selected, output);
            });
            StringAssert.StartsWith("AMBIGUOUS_MESH_COLLIDER", error.Message);
            Assert.That(AssetDatabase.IsValidFolder(output), Is.False);
            Assert.That(AssetDatabase.FindAssets("", new[] { _folder }).Length, Is.EqualTo(assetsBefore));
            Assert.That(selected.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
            Assert.That(collider.sharedMesh, Is.SameAs(mesh));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SingleRenderer_SeparateColliderHasUnambiguousOwnership(bool existingCopy)
        {
            var root = Keep(new GameObject("SingleMeshRoot"));
            var mesh = TestMesh();
            var selected = AddMesh(root, "Selected", mesh);
            var collision = new GameObject("Collision"); collision.transform.SetParent(root.transform, false);
            collision.AddComponent<MeshCollider>().sharedMesh = mesh;
            var result = existingCopy
                ? EditableMeshFactory.CreateForExistingCopy(root, selected, _folder)
                : EditableMeshFactory.CreateModelCopy(root, selected, _folder);
            if (!existingCopy) Keep(result.EditableRoot);
            Assert.That(result.EditableRoot.transform.Find("Collision").GetComponent<MeshCollider>().sharedMesh, Is.SameAs(result.EditableMesh));
        }

        private Mesh TestMesh()
        {
            var mesh = Keep(new Mesh { name = "SharedTriangle" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static MeshRenderer AddMesh(GameObject root, string name, Mesh mesh)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            return child.AddComponent<MeshRenderer>();
        }

        private T Keep<T>(T value) where T : Object { _objects.Add(value); return value; }
    }
}
