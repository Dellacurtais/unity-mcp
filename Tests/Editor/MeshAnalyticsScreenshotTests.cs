using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class MeshAnalyticsScreenshotTests
    {
        [Test]
        public void ScreenshotSchemas_PreserveOptionsInEveryLocatorBranch()
        {
            AssertCaptureSchema(new ScreenshotObjectTool(), expectedIsolationDefault: true);
            AssertCaptureSchema(new RenderObjectViewsTool(), expectedIsolationDefault: false);
        }

        [Test]
        public void ScreenshotTools_RegisterAsReadOnlyGraphicsOperations()
        {
            var registry = new McpToolRegistry();
            registry.RegisterRange(new IMcpTool[]
            {
                new ScreenshotObjectTool(),
                new RenderObjectViewsTool(),
            });

            Assert.That(registry.TryGet("screenshot_object", out _), Is.True);
            Assert.That(registry.TryGet("render_object_views", out _), Is.True);
            Assert.That(Bootstrap.ToolRegistry.TryGet("screenshot_object", out _), Is.True,
                "the live package bootstrap must publish the canonical MCP capture tool");
            var descriptor = registry.GetDescriptor("screenshot_object");
            Assert.That(descriptor.Capabilities, Is.EqualTo(McpCapability.Read));
            Assert.That(descriptor.ExecutionRequirements, Is.EqualTo(McpExecutionRequirement.MainThread));
            Assert.That(descriptor.HeadlessSafe, Is.False);
        }

        [UnityTest]
        public IEnumerator ScreenshotObject_RendersBatchedPngsWithoutChangingSource()
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = "ScreenshotSource";
            source.layer = 7;
            source.transform.SetPositionAndRotation(new Vector3(2f, 3f, 4f), Quaternion.Euler(10f, 20f, 30f));
            source.transform.localScale = new Vector3(1.5f, 2f, 0.75f);

            var positionBefore = source.transform.position;
            var rotationBefore = source.transform.rotation;
            var scaleBefore = source.transform.localScale;
            int layerBefore = source.layer;
            Material material = null;
            bool previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;

            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                material = CreateCaptureMaterial();
                source.GetComponent<Renderer>().sharedMaterial = material;
                yield return null;

                var result = new ScreenshotObjectTool().Execute(new JObject
                {
                    ["instanceId"] = source.GetMcpInstanceId(),
                    ["views"] = new JArray { "front", "iso" },
                    ["width"] = 96,
                    ["height"] = 80,
                });

                Assert.That(result.IsError, Is.False, result.Text);
                Assert.That((bool)result.Json["isolated"], Is.True);
                Assert.That((int)result.Json["width"], Is.EqualTo(96));
                Assert.That((int)result.Json["height"], Is.EqualTo(80));
                Assert.That(result.Json["views"], Has.Count.EqualTo(2));

                var images = result.ContentBlocks
                    .OfType<JObject>()
                    .Where(block => (string)block["type"] == "image")
                    .ToArray();
                Assert.That(images, Has.Length.EqualTo(2));
                foreach (var image in images)
                {
                    Assert.That((string)image["mimeType"], Is.EqualTo("image/png"));
                    var png = Convert.FromBase64String((string)image["data"]);
                    Assert.That(png.Take(8).ToArray(), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                    AssertPngHasVisualContent(png);
                }

                Assert.That(source.layer, Is.EqualTo(layerBefore));
                Assert.That(source.transform.position, Is.EqualTo(positionBefore));
                Assert.That(source.transform.rotation, Is.EqualTo(rotationBefore));
                Assert.That(source.transform.localScale, Is.EqualTo(scaleBefore));
                Assert.That(Resources.FindObjectsOfTypeAll<GameObject>()
                    .Any(go => go != null && go.name == "__McpObjectScreenshotClone"), Is.False,
                    "the isolated render clone must be destroyed before the MCP call returns");
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
                UnityEngine.Object.DestroyImmediate(source);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [UnityTest]
        public IEnumerator ScreenshotObject_CapturesPrefabAssetWithoutOpeningPrefabStage()
        {
            string folderName = "__McpObjectScreenshotTests_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            string prefabPath = folder + "/Capture.prefab";
            GameObject authoringRoot = null;
            bool previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);
                authoringRoot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                authoringRoot.name = "PrefabCaptureSource";
                var material = CreateCaptureMaterial();
                AssetDatabase.CreateAsset(material, folder + "/Capture.mat");
                authoringRoot.GetComponent<Renderer>().sharedMaterial = material;
                Assert.That(PrefabUtility.SaveAsPrefabAsset(authoringRoot, prefabPath), Is.Not.Null);
                UnityEngine.Object.DestroyImmediate(authoringRoot);
                authoringRoot = null;
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                // Previous fixtures may have switched render pipelines. Let the
                // Editor complete that transition before checking rendered pixels.
                yield return null;

                var result = new ScreenshotObjectTool().Execute(new JObject
                {
                    ["target"] = new JObject { ["assetPath"] = prefabPath },
                    ["width"] = 80,
                    ["height"] = 80,
                });

                Assert.That(result.IsError, Is.False, result.Text);
                Assert.That((bool)result.Json["isolated"], Is.True);
                Assert.That(result.Json["views"], Has.Count.EqualTo(1));
                var image = result.ContentBlocks.OfType<JObject>()
                    .Single(block => (string)block["type"] == "image");
                AssertPngHasVisualContent(Convert.FromBase64String((string)image["data"]));
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
                if (authoringRoot != null) UnityEngine.Object.DestroyImmediate(authoringRoot);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static Material CreateCaptureMaterial()
        {
            // Capture tests should not depend on the host project's lighting or
            // default material, which can change when other fixtures switch pipelines.
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var shaderName = pipeline == null ? "Unlit/Color"
                : pipeline.GetType().Name.Contains("HDRenderPipeline") ? "HDRP/Unlit"
                : "Universal Render Pipeline/Unlit";
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, "The capture fixture needs an unlit shader for the active pipeline.");
            var material = new Material(shader);
            foreach (var property in new[] { "_Color", "_BaseColor", "_UnlitColor" })
                if (material.HasProperty(property)) material.SetColor(property, Color.cyan);
            return material;
        }

        private static void AssertPngHasVisualContent(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.That(texture.LoadImage(png), Is.True);
                var pixels = texture.GetPixels32();
                var background = pixels[0];
                Assert.That(pixels.Any(pixel =>
                        Math.Abs(pixel.r - background.r) > 2 ||
                        Math.Abs(pixel.g - background.g) > 2 ||
                        Math.Abs(pixel.b - background.b) > 2),
                    Is.True, "the PNG must contain rendered object pixels, not only the clear color");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void AssertCaptureSchema(IMcpTool tool, bool expectedIsolationDefault)
        {
            var schema = tool.InputSchema;
            Assert.That(ToolSchemaValidator.ValidateSchema(schema, out var error), Is.True, error);

            var branches = (JArray)schema["oneOf"];
            Assert.That(branches, Has.Count.EqualTo(2));
            foreach (var branch in branches.OfType<JObject>())
            {
                var properties = (JObject)branch["properties"];
                foreach (var option in new[] { "target", "instanceId", "views", "width", "height", "overlay", "isolated" })
                    Assert.That(properties[option], Is.Not.Null, $"{tool.Name} branch dropped '{option}'");
                Assert.That((bool)properties["isolated"]["default"], Is.EqualTo(expectedIsolationDefault));
                Assert.That((int)properties["views"]["maxItems"], Is.EqualTo(6));
            }
        }
    }
}
