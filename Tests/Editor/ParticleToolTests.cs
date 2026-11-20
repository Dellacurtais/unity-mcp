using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class ParticleToolTests
    {
        private string _meshPath;
        private string _materialPath;

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_meshPath)) AssetDatabase.DeleteAsset(_meshPath);
            if (!string.IsNullOrEmpty(_materialPath)) AssetDatabase.DeleteAsset(_materialPath);
        }

        [Test]
        public void SetParticleModule_ConfiguresAdvancedModulesAndBursts()
        {
            var gameObject = new GameObject("ParticleToolAdvancedModules");
            var particleSystem = gameObject.AddComponent<ParticleSystem>();
            try
            {
                AssertOk(ParticleEditor.SetModule(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["module"] = "velocityOverLifetime",
                    ["fields"] = new JObject
                    {
                        ["enabled"] = true,
                        ["orbitalX"] = 1.5f,
                        ["orbitalY"] = 2.5f,
                        ["orbitalZ"] = 3.5f,
                        ["orbitalOffsetX"] = 4.5f,
                        ["orbitalOffsetY"] = 5.5f,
                        ["orbitalOffsetZ"] = 6.5f,
                        ["radial"] = 7.5f,
                        ["speedModifier"] = 0.75f,
                    },
                }));

                var velocity = particleSystem.velocityOverLifetime;
                Assert.That(velocity.orbitalX.constant, Is.EqualTo(1.5f));
                Assert.That(velocity.orbitalY.constant, Is.EqualTo(2.5f));
                Assert.That(velocity.orbitalZ.constant, Is.EqualTo(3.5f));
                Assert.That(velocity.orbitalOffsetX.constant, Is.EqualTo(4.5f));
                Assert.That(velocity.orbitalOffsetY.constant, Is.EqualTo(5.5f));
                Assert.That(velocity.orbitalOffsetZ.constant, Is.EqualTo(6.5f));
                Assert.That(velocity.radial.constant, Is.EqualTo(7.5f));
                Assert.That(velocity.speedModifier.constant, Is.EqualTo(0.75f));

                AssertOk(ParticleEditor.SetModule(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["module"] = "rotationOverLifetime",
                    ["fields"] = new JObject
                    {
                        ["enabled"] = true,
                        ["separateAxes"] = true,
                        ["x"] = 0.25f,
                        ["y"] = 0.5f,
                        ["z"] = 0.75f,
                    },
                }));

                var rotation = particleSystem.rotationOverLifetime;
                Assert.That(rotation.separateAxes, Is.True);
                Assert.That(rotation.x.constant, Is.EqualTo(0.25f));
                Assert.That(rotation.y.constant, Is.EqualTo(0.5f));
                Assert.That(rotation.z.constant, Is.EqualTo(0.75f));

                AssertOk(ParticleEditor.SetModule(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["module"] = "sizeOverLifetime",
                    ["fields"] = new JObject
                    {
                        ["enabled"] = true,
                        ["separateAxes"] = true,
                        ["x"] = 1f,
                        ["y"] = 2f,
                        ["z"] = 3f,
                    },
                }));

                var size = particleSystem.sizeOverLifetime;
                Assert.That(size.separateAxes, Is.True);
                Assert.That(size.x.constant, Is.EqualTo(1f));
                Assert.That(size.y.constant, Is.EqualTo(2f));
                Assert.That(size.z.constant, Is.EqualTo(3f));

                AssertOk(ParticleEditor.SetModule(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["module"] = "emission",
                    ["fields"] = new JObject
                    {
                        ["bursts"] = new JArray
                        {
                            new JObject
                            {
                                ["time"] = 0.5f,
                                ["min"] = 3,
                                ["max"] = 8,
                                ["cycles"] = 4,
                                ["interval"] = 0.25f,
                                ["probability"] = 0.6f,
                            },
                        },
                    },
                }));

                var bursts = new ParticleSystem.Burst[particleSystem.emission.burstCount];
                particleSystem.emission.GetBursts(bursts);
                Assert.That(bursts, Has.Length.EqualTo(1));
                Assert.That(bursts[0].time, Is.EqualTo(0.5f));
                Assert.That(bursts[0].minCount, Is.EqualTo(3));
                Assert.That(bursts[0].maxCount, Is.EqualTo(8));
                Assert.That(bursts[0].cycleCount, Is.EqualTo(4));
                Assert.That(bursts[0].repeatInterval, Is.EqualTo(0.25f));
                Assert.That(bursts[0].probability, Is.EqualTo(0.6f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void SetParticleModule_ConfiguresRendererAssetsAndSupportsUndo()
        {
            _meshPath = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneAnalyzerParticleToolTestMesh.asset");
            _materialPath = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneAnalyzerParticleToolTestMaterial.mat");
            var mesh = new Mesh { name = "ParticleToolMesh" };
            AssetDatabase.CreateAsset(mesh, _meshPath);
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { name = "ParticleToolMaterial" };
            AssetDatabase.CreateAsset(material, _materialPath);

            var gameObject = new GameObject("ParticleToolRenderer");
            gameObject.AddComponent<ParticleSystem>();
            var renderer = gameObject.GetComponent<ParticleSystemRenderer>();
            var initialSortingOrder = renderer.sortingOrder;
            try
            {
                Undo.IncrementCurrentGroup();
                var undoGroup = Undo.GetCurrentGroup();
                AssertOk(ParticleEditor.SetModule(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["module"] = "renderer",
                    ["fields"] = new JObject
                    {
                        ["renderMode"] = "Mesh",
                        ["alignment"] = "Local",
                        ["sortingOrder"] = 17,
                        ["minParticleSize"] = 0.02f,
                        ["maxParticleSize"] = 0.75f,
                        ["material"] = _materialPath,
                        ["trailMaterial"] = _materialPath,
                        ["mesh"] = _meshPath,
                    },
                }));
                Undo.CollapseUndoOperations(undoGroup);
                Undo.FlushUndoRecordObjects();

                Assert.That(renderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Mesh));
                Assert.That(renderer.alignment, Is.EqualTo(ParticleSystemRenderSpace.Local));
                Assert.That(renderer.sortingOrder, Is.EqualTo(17));
                Assert.That(renderer.minParticleSize, Is.EqualTo(0.02f));
                Assert.That(renderer.maxParticleSize, Is.EqualTo(0.75f));
                Assert.That(AssetDatabase.GetAssetPath(renderer.sharedMaterial), Is.EqualTo(_materialPath));
                Assert.That(AssetDatabase.GetAssetPath(renderer.trailMaterial), Is.EqualTo(_materialPath));
                Assert.That(AssetDatabase.GetAssetPath(renderer.mesh), Is.EqualTo(_meshPath));

                Undo.PerformUndo();
                Assert.That(renderer.sortingOrder, Is.EqualTo(initialSortingOrder));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ParticleToolSchemasAndPreview_ExposeDocumentedDefaults()
        {
            var colorSchema = (JObject)new CreateParticleSystemTool().InputSchema["properties"]["startColor"];
            Assert.That(colorSchema["oneOf"], Is.Not.Null);
            StringAssert.Contains("`default`", new CreateParticleSystemTool().Description);

            var previewTool = new PlayParticlePreviewTool();
            Assert.That((int)previewTool.InputSchema["properties"]["time"]["default"], Is.EqualTo(1));

            var gameObject = new GameObject("ParticleToolPreview");
            gameObject.AddComponent<ParticleSystem>();
            try
            {
                var result = ParticleEditor.PlayPreview(new JObject
                {
                    ["instanceId"] = gameObject.GetMcpInstanceId(),
                    ["action"] = "simulate",
                    ["time"] = 0,
                });
                AssertOk(result);
                Assert.That(result.Summary, Is.EqualTo("simulate 1s"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static void AssertOk(OpResult result)
        {
            Assert.That(result.Ok, Is.True, result.Error);
        }
    }
}
