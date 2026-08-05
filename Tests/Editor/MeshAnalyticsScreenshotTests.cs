using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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

        [Test]
        public void ScreenshotObject_RendersBatchedPngsWithoutChangingSource()
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

            try
            {
                var result = new ScreenshotObjectTool().Execute(new JObject
                {
                    ["instanceId"] = source.GetInstanceID(),
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
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void ScreenshotObject_CapturesPrefabAssetWithoutOpeningPrefabStage()
        {
            string folderName = "__McpObjectScreenshotTests_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            string prefabPath = folder + "/Capture.prefab";
            GameObject authoringRoot = null;
            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);
                authoringRoot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                authoringRoot.name = "PrefabCaptureSource";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(authoringRoot, prefabPath), Is.Not.Null);
                UnityEngine.Object.DestroyImmediate(authoringRoot);
                authoringRoot = null;

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
                if (authoringRoot != null) UnityEngine.Object.DestroyImmediate(authoringRoot);
                AssetDatabase.DeleteAsset(folder);
            }
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
