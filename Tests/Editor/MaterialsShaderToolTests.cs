using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.SystemTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class MaterialsShaderToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerMaterialAudit";
        private const string ShaderPath = Root + "/Audit.shader";
        private const string ShaderName = "SceneAnalyzerAudit/InternalTypedShader";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerMaterialAudit");
            File.WriteAllText(ShaderPath, ShaderSource);
            AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath), Is.Not.Null);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void ListShaders_FiltersByShaderNameAndPaginatesDeterministically()
        {
            var tool = new ListShadersTool();
            var result = tool.Execute(new JObject
            {
                ["contains"] = "InternalTypedShader",
                ["offset"] = 0,
                ["limit"] = 1,
            });

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json["total"], Is.EqualTo(1));
            Assert.That((int)result.Json["count"], Is.EqualTo(1));
            Assert.That((string)result.Json["shaders"][0]["name"], Is.EqualTo(ShaderName));
            Assert.That((string)result.Json["shaders"][0]["assetPath"], Is.EqualTo(ShaderPath));
        }

        [Test]
        public void CreateAndReadMaterial_ReportPersistedAssetAndAllUnity6000PropertyTypes()
        {
            var materialPath = Root + "/Created.mat";
            var created = new CreateMaterialTool().Execute(new JObject
            {
                ["assetPath"] = materialPath,
                ["shaderName"] = ShaderName,
            });

            Assert.That(created.IsError, Is.False, created.Text);
            Assert.That((bool)created.Json["assetCreated"], Is.True);
            Assert.That((bool)created.Json["savedAssets"], Is.True);
            Assert.That((bool)created.Json["undoRegistered"], Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(materialPath), Is.Not.Null);

            var read = new ReadMaterialTool().Execute(new JObject { ["assetPath"] = materialPath });
            Assert.That(read.IsError, Is.False, read.Text);
            var properties = ((JArray)read.Json["properties"])
                .OfType<JObject>()
                .ToDictionary(value => (string)value["name"]);
            Assert.That((string)properties["_ColorValue"]["type"], Is.EqualTo(ShaderPropertyType.Color.ToString()));
            Assert.That((string)properties["_VectorValue"]["type"], Is.EqualTo(ShaderPropertyType.Vector.ToString()));
            Assert.That((string)properties["_FloatValue"]["type"], Is.EqualTo(ShaderPropertyType.Float.ToString()));
            Assert.That((string)properties["_RangeValue"]["type"], Is.EqualTo(ShaderPropertyType.Range.ToString()));
            Assert.That((string)properties["_TextureValue"]["type"], Is.EqualTo(ShaderPropertyType.Texture.ToString()));
            Assert.That((string)properties["_IntegerValue"]["type"], Is.EqualTo(ShaderPropertyType.Int.ToString()));
            Assert.That((float)properties["_RangeValue"]["rangeLimits"]["min"], Is.EqualTo(0f));
            Assert.That((float)properties["_RangeValue"]["rangeLimits"]["max"], Is.EqualTo(1f));
        }

        [Test]
        public void SetMaterialPropertyTyped_RejectsInvalidValuesBeforeMutationAndUndoRestoresValidChange()
        {
            var materialPath = CreateMaterialAsset("Typed.mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            material.SetColor("_ColorValue", new Color(0.1f, 0.2f, 0.3f, 1f));
            material.SetFloat("_RangeValue", 0.5f);
            material.SetVector("_VectorValue", new Vector4(1f, 2f, 3f, 4f));
            EditorUtility.SetDirty(material);

            var tool = new SetMaterialPropertyTypedTool();
            var badColor = tool.Execute(new JObject
            {
                ["assetPath"] = materialPath,
                ["propertyName"] = "_ColorValue",
                ["value"] = "not-a-color",
            });
            Assert.That(badColor.IsError, Is.True);
            Assert.That(material.GetColor("_ColorValue"), Is.EqualTo(new Color(0.1f, 0.2f, 0.3f, 1f)));

            var badRange = tool.Execute(new JObject
            {
                ["assetPath"] = materialPath,
                ["propertyName"] = "_RangeValue",
                ["value"] = 2f,
            });
            Assert.That(badRange.IsError, Is.True);
            Assert.That(material.GetFloat("_RangeValue"), Is.EqualTo(0.5f));

            var badVector = tool.Execute(new JObject
            {
                ["assetPath"] = materialPath,
                ["propertyName"] = "_VectorValue",
                ["value"] = new JArray(1f, 2f, 3f, 4f, 5f),
            });
            Assert.That(badVector.IsError, Is.True);
            Assert.That(material.GetVector("_VectorValue"), Is.EqualTo(new Vector4(1f, 2f, 3f, 4f)));

            var packageMaterial = tool.Execute(new JObject
            {
                ["assetPath"] = "Packages/com.unity.shadergraph/Editor/NotEditable.mat",
                ["propertyName"] = "_ColorValue",
                ["value"] = "#ffffffff",
            });
            Assert.That(packageMaterial.IsError, Is.True);
            Assert.That((string)packageMaterial.Json["code"], Is.EqualTo("BAD_ASSET_PATH"));

            var changed = tool.Execute(new JObject
            {
                ["assetPath"] = materialPath,
                ["propertyName"] = "_ColorValue",
                ["value"] = "#336699ff",
            });
            Assert.That(changed.IsError, Is.False, changed.Text);
            Assert.That(changed.Json["effectiveValue"], Is.TypeOf<JObject>());
            Assert.That(material.GetColor("_ColorValue"), Is.EqualTo(new Color(0.2f, 0.4f, 0.6f, 1f)));

            Undo.PerformUndo();
            Assert.That(material.GetColor("_ColorValue"), Is.EqualTo(new Color(0.1f, 0.2f, 0.3f, 1f)));
        }

        [Test]
        public void SetMaterial_AcceptsGuidOrPathRecordsPrefabOverrideAndIsUndoable()
        {
            var originalPath = CreateMaterialAsset("Original.mat");
            var replacementPath = CreateMaterialAsset("Replacement.mat");
            var original = AssetDatabase.LoadAssetAtPath<Material>(originalPath);
            var replacement = AssetDatabase.LoadAssetAtPath<Material>(replacementPath);

            var prefabSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prefabSource.GetComponent<Renderer>().sharedMaterial = original;
            var prefabPath = Root + "/Renderer.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
            Object.DestroyImmediate(prefabSource);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            var renderer = instance.GetComponent<Renderer>();

            var tool = new SetMaterialTool();
            var args = new JObject
            {
                ["instanceId"] = instance.GetInstanceID(),
                ["material"] = replacementPath,
                ["slotIndex"] = 0,
            };
            Assert.That(ToolSchemaValidator.ValidateArguments(args, tool.InputSchema, out var schemaError), Is.True, schemaError);
            var result = tool.Execute(args);
            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json["slotIndex"], Is.EqualTo(0));
            Assert.That(renderer.sharedMaterial, Is.SameAs(replacement));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            Assert.That(instance.scene.isDirty, Is.True);

            Undo.PerformUndo();
            Assert.That(renderer.sharedMaterial, Is.SameAs(original));
        }

        private static string CreateMaterialAsset(string fileName)
        {
            var path = Root + "/" + fileName;
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath));
            AssetDatabase.CreateAsset(material, path);
            return path;
        }

        private const string ShaderSource = @"Shader ""SceneAnalyzerAudit/InternalTypedShader""
{
    Properties
    {
        _ColorValue (""Color"", Color) = (1, 0.5, 0.25, 1)
        _VectorValue (""Vector"", Vector) = (1, 2, 3, 4)
        _FloatValue (""Float"", Float) = 1
        _RangeValue (""Range"", Range(0, 1)) = 0.5
        _TextureValue (""Texture"", 2D) = ""white"" {}
        _IntegerValue (""Integer"", Integer) = 2
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings vert(Attributes input) { Varyings output; output.positionCS = input.positionOS; return output; }
            half4 frag(Varyings input) : SV_Target { return half4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}";
    }
}
