using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.SystemTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class LightingRenderingToolTests
    {
        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
        }

        [Test]
        public void CreateLight_UsesUnity6000TypesAndRejectsInvalidArgumentsBeforeCreation()
        {
            var schemaTypes = ((JArray)new CreateLightTool().InputSchema["properties"]["type"]["enum"])
                .Values<string>()
                .ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                "Spot", "Directional", "Point", "Rectangle", "Disc", "Pyramid", "Box", "Tube",
            }, schemaTypes);

            var legacyAlias = LightFactory.Create(new JObject
            {
                ["type"] = "Area",
                ["name"] = "Legacy Area Alias",
            });
            AssertOk(legacyAlias);
            var legacyGameObject = GameObject.Find("Legacy Area Alias");
            Assert.That(legacyGameObject.GetComponent<Light>().type, Is.EqualTo(LightType.Rectangle));

            var countBefore = UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var invalidParent = LightFactory.Create(new JObject
            {
                ["type"] = "Point",
                ["name"] = "Must Not Exist",
                ["parent"] = int.MaxValue,
            });
            Assert.That(invalidParent.Ok, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(countBefore));

            var invalidEnum = LightFactory.Create(new JObject
            {
                ["type"] = "Point",
                ["name"] = "Invalid Shadows Must Not Exist",
                ["shadows"] = "Ultra",
            });
            Assert.That(invalidEnum.Ok, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(countBefore));

            var areaObjectSchema = (JObject)new CreateLightTool().InputSchema["properties"]["areaSize"]["oneOf"][1];
            CollectionAssert.AreEquivalent(new[] { "x", "y" }, ((JArray)areaObjectSchema["required"]).Values<string>());
            var incompleteAreaSize = LightFactory.Create(new JObject
            {
                ["type"] = "Rectangle",
                ["name"] = "Incomplete Area Must Not Exist",
                ["areaSize"] = new JObject { ["x"] = 4f },
            });
            Assert.That(incompleteAreaSize.Ok, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(countBefore));
        }

        [Test]
        public void CreateLight_ConfiguresTypeSpecificPropertiesAndReturnsStructuredData()
        {
            var result = LightFactory.Create(new JObject
            {
                ["type"] = "Spot",
                ["name"] = "Audited Spot",
                ["intensity"] = 3.5f,
                ["range"] = 18f,
                ["spotAngle"] = 55f,
                ["innerSpotAngle"] = 20f,
                ["color"] = "#80c0ffff",
                ["shadows"] = "Hard",
                ["mode"] = "Mixed",
                ["position"] = new JArray(1f, 2f, 3f),
                ["rotation"] = new JArray(10f, 20f, 30f),
            });

            AssertOk(result);
            var gameObject = GameObject.Find("Audited Spot");
            var light = gameObject.GetComponent<Light>();
            Assert.That(light.type, Is.EqualTo(LightType.Spot));
            Assert.That(light.spotAngle, Is.EqualTo(55f));
            Assert.That(light.innerSpotAngle, Is.EqualTo(20f));
            Assert.That(light.range, Is.EqualTo(18f));
            Assert.That(light.shadows, Is.EqualTo(LightShadows.Hard));
            Assert.That(light.lightmapBakeType, Is.EqualTo(LightmapBakeType.Mixed));
            Assert.That(light.transform.localPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));

            var data = result.Data as JObject;
            Assert.That(data, Is.Not.Null);
            Assert.That((string)data["type"], Is.EqualTo("Spot"));
            Assert.That((int)data["lightInstanceId"], Is.EqualTo(light.GetInstanceID()));
            Assert.That((float)data["spotAngle"], Is.EqualTo(55f));

            var parent = new GameObject("Audited Light Parent");
            var rectangle = LightFactory.Create(new JObject
            {
                ["type"] = "Rectangle",
                ["areaSize"] = new JArray(4f, 2f),
                ["parent"] = parent.GetInstanceID(),
            });
            AssertOk(rectangle);
            var rectangleGameObject = GameObject.Find("Rectangle Light");
            var rectangleLight = rectangleGameObject.GetComponent<Light>();
            Assert.That(rectangleLight.areaSize, Is.EqualTo(new Vector2(4f, 2f)));
            Assert.That(rectangleGameObject.transform.parent, Is.EqualTo(parent.transform));

            Undo.PerformUndo();
            Assert.That(GameObject.Find("Rectangle Light"), Is.Null);
            Assert.That(GameObject.Find("Audited Light Parent"), Is.SameAs(parent));
        }

        [Test]
        public void SetRenderSettings_ValidatesEverythingBeforeMutating()
        {
            RenderSettings.ambientIntensity = 0.75f;
            RenderSettings.fog = false;

            var missingSkybox = RenderSettingsEditor.Apply(new JObject
            {
                ["ambientIntensity"] = 2f,
                ["skybox"] = "Assets/MissingSkybox.mat",
            });
            Assert.That(missingSkybox.Ok, Is.False);
            Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(0.75f));

            var invalidEnum = RenderSettingsEditor.Apply(new JObject
            {
                ["ambientMode"] = "NotAnAmbientMode",
                ["fog"] = true,
            });
            Assert.That(invalidEnum.Ok, Is.False);
            Assert.That(RenderSettings.fog, Is.False);

            var empty = RenderSettingsEditor.Apply(new JObject());
            Assert.That(empty.Ok, Is.False);
        }

        [Test]
        public void SetRenderSettings_AppliesStructuredValuesAndUndoRestoresThem()
        {
            var beforeAmbientMode = RenderSettings.ambientMode;
            var beforeAmbientLight = RenderSettings.ambientLight;
            var beforeAmbientIntensity = RenderSettings.ambientIntensity;
            var beforeFog = RenderSettings.fog;
            var beforeFogMode = RenderSettings.fogMode;
            var beforeFogDensity = RenderSettings.fogDensity;
            var beforeFogStart = RenderSettings.fogStartDistance;
            var beforeFogEnd = RenderSettings.fogEndDistance;
            var beforeReflection = RenderSettings.reflectionIntensity;

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = RenderSettingsEditor.Apply(new JObject
            {
                ["ambientMode"] = "Flat",
                ["ambientLight"] = "#334d66ff",
                ["ambientIntensity"] = 1.25f,
                ["fog"] = true,
                ["fogColor"] = new JArray(0.1f, 0.2f, 0.3f, 1f),
                ["fogMode"] = "Linear",
                ["fogDensity"] = 0.02f,
                ["fogStartDistance"] = 5f,
                ["fogEndDistance"] = 80f,
                ["reflectionIntensity"] = 0.65f,
            });
            Undo.CollapseUndoOperations(group);

            AssertOk(result);
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(AmbientMode.Flat));
            Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(1.25f));
            Assert.That(RenderSettings.fog, Is.True);
            Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.Linear));
            Assert.That(RenderSettings.fogStartDistance, Is.EqualTo(5f));
            Assert.That(RenderSettings.fogEndDistance, Is.EqualTo(80f));
            Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(0.65f));
            Assert.That(result.Data, Is.TypeOf<JObject>());
            Assert.That(SceneManager.GetActiveScene().isDirty, Is.True);

            Undo.PerformUndo();
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(beforeAmbientMode));
            Assert.That(RenderSettings.ambientLight, Is.EqualTo(beforeAmbientLight));
            Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(beforeAmbientIntensity));
            Assert.That(RenderSettings.fog, Is.EqualTo(beforeFog));
            Assert.That(RenderSettings.fogMode, Is.EqualTo(beforeFogMode));
            Assert.That(RenderSettings.fogDensity, Is.EqualTo(beforeFogDensity));
            Assert.That(RenderSettings.fogStartDistance, Is.EqualTo(beforeFogStart));
            Assert.That(RenderSettings.fogEndDistance, Is.EqualTo(beforeFogEnd));
            Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(beforeReflection));
        }

        [Test]
        public void ContextTools_ReportActiveSceneDetailsAndEveryQualityPipelineOverride()
        {
            var lightObject = new GameObject("Context Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Rectangle;
            light.areaSize = new Vector2(3f, 2f);
            light.intensity = 4f;
            light.enabled = false;

            var probeObject = new GameObject("Context Reflection Probe");
            probeObject.AddComponent<ReflectionProbe>();
            var groupObject = new GameObject("Context Probe Group");
            groupObject.AddComponent<LightProbeGroup>().probePositions = new[] { Vector3.zero, Vector3.one };

            var lighting = new GetLightingContextTool().Execute(new JObject());
            Assert.That(lighting.IsError, Is.False, lighting.Text);
            var lightingJson = (JObject)lighting.Json;
            Assert.That((string)lightingJson["activeScene"]["name"], Is.EqualTo(SceneManager.GetActiveScene().name));
            Assert.That((int)lightingJson["lightCount"], Is.EqualTo(1));
            Assert.That((int)lightingJson["reflectionProbeCount"], Is.EqualTo(1));
            Assert.That((int)lightingJson["lightProbeGroupCount"], Is.EqualTo(1));
            var lightJson = (JObject)((JArray)lightingJson["lights"])[0];
            Assert.That((bool)lightJson["enabled"], Is.False);
            Assert.That((string)lightJson["type"], Is.EqualTo("Rectangle"));
            Assert.That((float)lightJson["areaSize"]["x"], Is.EqualTo(3f));

            var pipeline = new GetRenderPipelineContextTool().Execute(new JObject());
            Assert.That(pipeline.IsError, Is.False, pipeline.Text);
            var qualityLevels = (JArray)pipeline.Json["qualityLevels"];
            Assert.That(qualityLevels, Is.Not.Null);
            Assert.That(qualityLevels.Count, Is.EqualTo(QualitySettings.names.Length));
            for (var index = 0; index < qualityLevels.Count; index++)
            {
                Assert.That((int)qualityLevels[index]["index"], Is.EqualTo(index));
                Assert.That((string)qualityLevels[index]["name"], Is.EqualTo(QualitySettings.names[index]));
                Assert.That(qualityLevels[index]["pipeline"], Is.Not.Null);
            }
        }

        [Test]
        public void OptionalSrpValidators_ReflectInstalledPackagesAndReportEveryAssignment()
        {
            foreach (var expectation in new[]
            {
                new { Module = "urp", Tool = "validate_urp_setup", Package = "com.unity.render-pipelines.universal", MissingAssetCode = "NO_URP_PIPELINE_ASSET" },
                new { Module = "hdrp", Tool = "validate_hdrp_setup", Package = "com.unity.render-pipelines.high-definition", MissingAssetCode = "NO_HDRP_PIPELINE_ASSET" },
            })
            {
                var package = PackageInfo.FindForPackageName(expectation.Package);
                var module = OptionalModuleRegistry.All.SingleOrDefault(value => value.Id == expectation.Module);
                if (package == null)
                {
                    Assert.That(module, Is.Null);
                    Assert.That(Bootstrap.ToolRegistry.TryGet(expectation.Tool, out _), Is.False);
                    continue;
                }

                Assert.That(module, Is.Not.Null, $"optional module {expectation.Module} was not registered");
                Assert.That(module.PackageId, Is.EqualTo(expectation.Package));
                Assert.That(module.PackageVersion, Is.EqualTo(package.version));
                CollectionAssert.Contains(module.Tools, expectation.Tool);

                Assert.That(Bootstrap.ToolRegistry.TryGet(expectation.Tool, out var tool), Is.True);
                Assert.That(tool.IsWrite, Is.False);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var schemaError), Is.True, schemaError);

                var result = tool.Execute(new JObject());
                Assert.That(result.IsError, Is.False, result.Text);
                var data = result.Json as JObject;
                Assert.That(data, Is.Not.Null);
                Assert.That((string)data["pipelineId"], Is.EqualTo(expectation.Module));
                Assert.That((string)data["package"]["name"], Is.EqualTo(expectation.Package));
                Assert.That((string)data["package"]["version"], Is.EqualTo(package.version));
                Assert.That(data["current"], Is.TypeOf<JObject>());
                Assert.That(data["default"], Is.TypeOf<JObject>());
                Assert.That(data["activeQuality"], Is.TypeOf<JObject>());
                Assert.That(data["projectAssets"], Is.TypeOf<JArray>());

                var qualityLevels = data["qualityLevels"] as JArray;
                Assert.That(qualityLevels, Is.Not.Null);
                Assert.That(qualityLevels.Count, Is.EqualTo(QualitySettings.names.Length));
                for (var index = 0; index < qualityLevels.Count; index++)
                {
                    Assert.That((int)qualityLevels[index]["index"], Is.EqualTo(index));
                    Assert.That((string)qualityLevels[index]["name"], Is.EqualTo(QualitySettings.names[index]));
                    Assert.That(qualityLevels[index]["override"], Is.TypeOf<JObject>());
                    Assert.That(qualityLevels[index]["effective"], Is.TypeOf<JObject>());
                }

                if (((JArray)data["projectAssets"]).Count == 0)
                    Assert.That(((JArray)data["issues"]).Any(issue => (string)issue["code"] == expectation.MissingAssetCode), Is.True);
            }
        }

        [Test]
        public void OptionalSrpValidation_ValidReflectsDefaultAndPerQualityAssignments()
        {
            var expectedTypeNames = new[] { typeof(AuditRenderPipelineAsset).FullName };
            var originalQuality = QualitySettings.GetQualityLevel();
            var originalDefault = GraphicsSettings.defaultRenderPipeline;
            var originalOverrides = Enumerable.Range(0, QualitySettings.names.Length)
                .Select(QualitySettings.GetRenderPipelineAssetAt)
                .ToArray();
            var expectedPath = AssetDatabase.GenerateUniqueAssetPath("Assets/AuditRenderPipeline.asset");
            var alternatePath = AssetDatabase.GenerateUniqueAssetPath("Assets/AlternateAuditRenderPipeline.asset");
            var expected = ScriptableObject.CreateInstance<AuditRenderPipelineAsset>();
            var alternate = ScriptableObject.CreateInstance<AlternateAuditRenderPipelineAsset>();
            AssetDatabase.CreateAsset(expected, expectedPath);
            AssetDatabase.CreateAsset(alternate, alternatePath);

            try
            {
                GraphicsSettings.defaultRenderPipeline = expected;
                for (var index = 0; index < QualitySettings.names.Length; index++)
                {
                    QualitySettings.SetQualityLevel(index, false);
                    QualitySettings.renderPipeline = null;
                }
                QualitySettings.SetQualityLevel(originalQuality, false);

                var valid = Optional.OptionalAssetToolSupport.ValidateRenderPipelineSetup(
                    "audit",
                    "com.unity.render-pipelines.core",
                    expectedTypeNames,
                    "NO_AUDIT_PIPELINE_ASSET",
                    "AUDIT_NOT_CURRENT_PIPELINE");
                Assert.That(valid.IsError, Is.False, valid.Text);
                Assert.That((bool)valid.Json["hasProjectAsset"], Is.True);
                Assert.That(valid.Json["currentMatches"].Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That((bool)valid.Json["allQualityLevelsMatch"], Is.True);
                Assert.That((bool)valid.Json["assignmentsValid"], Is.True);
                Assert.That((bool)valid.Json["valid"], Is.True);

                var mismatchIndex = QualitySettings.names.Length > 1
                    ? (originalQuality + 1) % QualitySettings.names.Length
                    : originalQuality;
                QualitySettings.SetQualityLevel(mismatchIndex, false);
                QualitySettings.renderPipeline = alternate;
                QualitySettings.SetQualityLevel(originalQuality, false);

                var invalidOverride = Optional.OptionalAssetToolSupport.ValidateRenderPipelineSetup(
                    "audit",
                    "com.unity.render-pipelines.core",
                    expectedTypeNames,
                    "NO_AUDIT_PIPELINE_ASSET",
                    "AUDIT_NOT_CURRENT_PIPELINE");
                Assert.That((bool)invalidOverride.Json["allQualityLevelsMatch"], Is.False);
                Assert.That((bool)invalidOverride.Json["assignmentsValid"], Is.False);
                Assert.That((bool)invalidOverride.Json["valid"], Is.False);
                Assert.That((bool)invalidOverride.Json["qualityLevels"][mismatchIndex]["matches"], Is.False);

                GraphicsSettings.defaultRenderPipeline = null;
                QualitySettings.SetQualityLevel(originalQuality, false);
                QualitySettings.renderPipeline = null;
                var unusedAsset = Optional.OptionalAssetToolSupport.ValidateRenderPipelineSetup(
                    "audit",
                    "com.unity.render-pipelines.core",
                    expectedTypeNames,
                    "NO_AUDIT_PIPELINE_ASSET",
                    "AUDIT_NOT_CURRENT_PIPELINE");
                Assert.That((bool)unusedAsset.Json["hasProjectAsset"], Is.True);
                Assert.That((bool)unusedAsset.Json["currentMatches"], Is.False);
                Assert.That((bool)unusedAsset.Json["valid"], Is.False);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = originalDefault;
                for (var index = 0; index < originalOverrides.Length; index++)
                {
                    QualitySettings.SetQualityLevel(index, false);
                    QualitySettings.renderPipeline = originalOverrides[index];
                }
                QualitySettings.SetQualityLevel(originalQuality, false);
                AssetDatabase.DeleteAsset(expectedPath);
                AssetDatabase.DeleteAsset(alternatePath);
            }
        }

        private static void AssertOk(OpResult result)
        {
            Assert.That(result.Ok, Is.True, result.Error);
        }
    }
}
