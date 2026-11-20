using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Maicon.SceneAnalyzer.Editor.Optional.ShaderGraph;
using Newtonsoft.Json;
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
            Assert.That(Version.Parse(package.version.Split('-')[0]), Is.GreaterThanOrEqualTo(new Version(17, 0, 0)));
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
        public void Write_RejectsMalformedSourceBeforeMutation()
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

            var rejected = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["source"] = "m_ObjectId\nm_Type",
                ["overwrite"] = true,
                ["expectedSha256"] = hash,
            });
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
        }

        [Test]
        public void Write_ValidSourceRoundTripSucceedsWithoutImportErrors()
        {
            CopyTemplate();
            var before = File.ReadAllBytes(GraphPath);
            var read = ReadCurrent();
            var hash = (string)read["sha256"];
            var validSource = File.ReadAllText(GraphPath);
            var written = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath, ["source"] = validSource,
                ["overwrite"] = true, ["expectedSha256"] = hash,
            });
            Assert.That(written.IsError, Is.False, written.Text);
            Assert.That((int)written.Json["importErrorCount"], Is.Zero);
            Assert.That((int)written.Json["shaderErrorCount"], Is.Zero);
            Assert.That((string)written.Json["afterSha256"], Is.EqualTo(hash));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(GraphPath));
        }

        [Test]
        public void Write_SemanticallyInvalidGraphRollsBackAfterRealUnityImportError()
        {
            CopyTemplate();
            var before = File.ReadAllBytes(GraphPath);
            var read = ReadCurrent();
            var hash = (string)read["sha256"];
            var invalidSource = WithInvalidConnectedSwizzle(File.ReadAllText(GraphPath));
            // Shader Graph 17.3 SwizzleNode.ValidateNode reports this error;
            // ShaderSubGraphImporter forwards errors from reachable nodes via
            // AssetImportContext.LogImportError. Do not suppress unrelated logs.
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("Sub Graph at .* has .*error.*Invalid mask", RegexOptions.Singleline));
            var failedImport = new WriteShaderGraphSourceTool().Execute(new JObject
            {
                ["assetPath"] = GraphPath,
                ["source"] = invalidSource,
                ["overwrite"] = true,
                ["expectedSha256"] = hash,
            });
            Assert.That(failedImport.IsError, Is.True, failedImport.Text);
            Assert.That((string)failedImport.Json["code"], Is.EqualTo("WRITE_SHADER_GRAPH_FAILED"));
            Assert.That((bool)failedImport.Json["data"]["failedInspection"]["sourceValid"], Is.True,
                "The failure must come from Unity's semantic import, not the MCP JSON preflight.");
            Assert.That((int)failedImport.Json["data"]["failedInspection"]["importErrorCount"], Is.GreaterThan(0));
            StringAssert.Contains("Invalid mask", failedImport.Json["data"]["failedInspection"]["importMessages"].ToString());
            Assert.That((bool)failedImport.Json["data"]["rollback"]["verified"], Is.True);
            Assert.That((string)failedImport.Json["data"]["rollback"]["restoredSha256"], Is.EqualTo(hash));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(GraphPath));
            Assert.That((int)ReadCurrent()["importErrorCount"], Is.Zero, "Rollback must restore a healthy imported graph.");
        }

        private static JObject ReadCurrent()
        {
            var read = new ReadShaderGraphSourceTool().Execute(new JObject { ["assetPath"] = GraphPath, ["maxCharacters"] = 500000 });
            Assert.That(read.IsError, Is.False, read.Text);
            Assert.That((bool)read.Json["sourceValid"], Is.True);
            return (JObject)read.Json;
        }

        private static string WithInvalidConnectedSwizzle(string source)
        {
            var records = new List<JObject>();
            using (var text = new StringReader(source))
            using (var reader = new JsonTextReader(text) { SupportMultipleContent = true })
                while (reader.Read()) records.Add(JObject.Load(reader));
            var graph = records[0];
            var outputId = (string)graph["m_OutputNode"]["m_Id"];
            var edge = ((JArray)graph["m_Edges"]).OfType<JObject>()
                .FirstOrDefault(value => (string)value["m_InputSlot"]?["m_Node"]?["m_Id"] == outputId);
            Assert.That(edge, Is.Not.Null, "The official template must contain a connected output slot.");
            var nodeId = Guid.NewGuid().ToString("N");
            ((JArray)graph["m_Nodes"]).Add(new JObject { ["m_Id"] = nodeId });
            edge["m_OutputSlot"] = new JObject { ["m_Node"] = new JObject { ["m_Id"] = nodeId }, ["m_SlotId"] = 1 };
            records.Add(new JObject
            {
                ["m_SGVersion"] = 1, ["m_Type"] = "UnityEditor.ShaderGraph.SwizzleNode", ["m_ObjectId"] = nodeId,
                ["m_Group"] = new JObject { ["m_Id"] = "" }, ["m_Name"] = "Deliberately invalid mask",
                ["m_Slots"] = new JArray(), ["_maskInput"] = "!",
            });
            // GraphData.OnAfterMultiDeserialize rebuilds Swizzle's input/output
            // slots before resolving edges. A connected invalid mask gives an
            // explicit semantic error without relying on missing scripts or URP.
            return string.Join("\n\n", records.Select(record => record.ToString(Formatting.Indented)));
        }

        private static void CopyTemplate()
        {
            Assert.That(File.Exists(TemplatePath), Is.True, TemplatePath);
            File.Copy(TemplatePath, GraphPath, true);
            AssetDatabase.ImportAsset(GraphPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var imported = ReadCurrent();
            Assert.That((int)imported["importErrorCount"], Is.Zero, "The valid template must import cleanly before testing writes.");
            Assert.That(imported["importedAsset"].Type, Is.Not.EqualTo(JTokenType.Null));
        }
    }
}
