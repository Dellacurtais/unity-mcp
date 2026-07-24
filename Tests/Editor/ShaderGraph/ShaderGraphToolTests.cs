using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Optional.ShaderGraph;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.ShaderGraph.Tests
{
    public sealed class ShaderGraphToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerShaderGraphAudit";
        private const string GraphPath = Root + "/Audit.shadersubgraph";
        private const string TemplatePath = "Packages/com.unity.shadergraph/GraphTemplates/Subgraphs/UV Scale.shadersubgraph";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerShaderGraphAudit");
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void OptionalModule_IsGatedByInstalledShaderGraph17AndRegistered()
        {
            var package = PackageInfo.FindForPackageName("com.unity.shadergraph");
            Assert.That(package, Is.Not.Null);
            Assert.That(package.version, Is.EqualTo("17.3.0"));
            var module = Maicon.SceneAnalyzer.Editor.Mcp.OptionalModuleRegistry.All.Single(value => value.Id == "shadergraph");
            Assert.That(module.PackageVersion, Is.EqualTo(package.version));
            CollectionAssert.AreEquivalent(new[]
            {
                "get_shader_graph_context",
                "list_shader_graph_assets",
                "read_shader_graph_source",
                "validate_shader_graph_assets",
                "write_shader_graph_source",
            }, module.Tools);
        }

        [Test]
        public void ContextAndList_CountOnlyEditableAssetsAndAgree()
        {
            CopyTemplate();
            var expected = AssetDatabase.FindAssets("t:Shader")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Concat(AssetDatabase.FindAssets("t:DefaultAsset").Select(AssetDatabase.GUIDToAssetPath))
                .Where(path => path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".shadergraph", System.StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".shadersubgraph", System.StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            var context = new GetShaderGraphContextTool().Execute(new JObject());
            var listed = new ListShaderGraphAssetsTool().Execute(new JObject());
            Assert.That(context.IsError, Is.False, context.Text);
            Assert.That(listed.IsError, Is.False, listed.Text);
            Assert.That((int)context.Json["graphCount"] + (int)context.Json["subGraphCount"], Is.EqualTo(expected.Length));
            Assert.That(((JArray)listed.Json["assets"]).Count, Is.EqualTo(expected.Length));
            Assert.That(((JArray)listed.Json["assets"]).All(item => ((string)item["assetPath"]).StartsWith("Assets/")), Is.True);
        }

        [Test]
        public void Write_RejectsMarkerOnlySourceBeforeMutationAndRollsBackWhenUnityImportFails()
        {
            CopyTemplate();
            var before = File.ReadAllBytes(GraphPath);
            var read = new ReadShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["maxCharacters"] = 500000,
            });
            Assert.That(read.IsError, Is.False, read.Text);
            Assert.That((bool)read.Json["sourceValid"], Is.True);
            Assert.That((int)read.Json["objectRecordCount"], Is.GreaterThan(1));
            var hash = (string)read.Json["sha256"];

            LogAssert.ignoreFailingMessages = true;
            var rejected = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["source"] = "m_ObjectId\nm_Type",
                ["overwrite"] = true,
                ["expectedSha256"] = hash,
            });
            LogAssert.ignoreFailingMessages = false;
            Assert.That(rejected.IsError, Is.True);
            Assert.That((string)rejected.Json["code"], Is.EqualTo("INVALID_SHADER_GRAPH_SOURCE"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(GraphPath));

            var missingObjectId = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["source"] = "{\"m_Type\":\"UnityEditor.ShaderGraph.GraphData\"}",
                ["overwrite"] = true,
                ["expectedSha256"] = hash,
            });
            Assert.That(missingObjectId.IsError, Is.True);
            Assert.That((string)missingObjectId.Json["code"], Is.EqualTo("INVALID_SHADER_GRAPH_SOURCE"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(GraphPath));

            var validSource = File.ReadAllText(GraphPath);
            LogAssert.ignoreFailingMessages = true;
            var failedImport = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["source"] = validSource,
                ["overwrite"] = true,
                ["expectedSha256"] = hash,
            });
            LogAssert.ignoreFailingMessages = false;
            Assert.That(failedImport.IsError, Is.True);
            Assert.That((string)failedImport.Json["code"], Is.EqualTo("WRITE_SHADER_GRAPH_FAILED"));
            Assert.That((int)failedImport.Json["data"]["failedInspection"]["importErrorCount"], Is.GreaterThan(0));
            Assert.That((bool)failedImport.Json["data"]["rollback"]["verified"], Is.True);
            Assert.That((string)failedImport.Json["data"]["rollback"]["restoredSha256"], Is.EqualTo(hash));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(GraphPath));
        }

        private static void CopyTemplate()
        {
            Assert.That(File.Exists(TemplatePath), Is.True, TemplatePath);
            File.Copy(TemplatePath, GraphPath, true);
            LogAssert.ignoreFailingMessages = true;
            AssetDatabase.ImportAsset(GraphPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            LogAssert.ignoreFailingMessages = false;
        }
    }
}
