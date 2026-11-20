using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.SystemTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class AnimationMecanimToolTests
    {
        private static string Root;
        private static string ControllerPath => Root + "/Audit.controller";
        private static string ClipPath => Root + "/Audit.anim";
        private static string MaskPath => Root + "/Audit.mask";
        private static string AvatarPath => Root + "/AuditAvatar.asset";
        private static string PrefabPath => Root + "/Animated.prefab";

        private AnimatorController _controller;
        private AnimationClip _clip;
        private AvatarMask _mask;

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Do not reuse paths whose importer/native objects can still be
            // pending deletion from an earlier test in this Editor session.
            var folderName = "__SceneAnalyzerAnimationAudit_" + Guid.NewGuid().ToString("N");
            Root = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);

            _controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            Assert.That(_controller, Is.Not.Null);

            _clip = new AnimationClip { name = "Audit", frameRate = 60f };
            AssetDatabase.CreateAsset(_clip, ClipPath);
            AnimationUtility.SetEditorCurve(
                _clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Linear(0f, 0f, 1f, 1f));

            _mask = new AvatarMask { name = "Audit" };
            AssetDatabase.CreateAsset(_mask, MaskPath);

            var avatarRoot = new GameObject("AvatarRoot");
            var bone = new GameObject("Bone");
            bone.transform.SetParent(avatarRoot.transform);
            var avatar = AvatarBuilder.BuildGenericAvatar(avatarRoot, string.Empty);
            Assert.That(avatar, Is.Not.Null);
            avatar.name = "AuditAvatar";
            AssetDatabase.CreateAsset(avatar, AvatarPath);
            UnityEngine.Object.DestroyImmediate(avatarRoot);

            AssetDatabase.SaveAssets();
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void SchemasDescriptorsAndRegistry_CoverNineteenToolsAndSeparateRuntimeSceneAndAssetWrites()
        {
            IMcpTool[] tools =
            {
                new ListAnimatorControllersTool(),
                new ReadAnimatorControllerTool(),
                new ReadAnimationClipTool(),
                new ReadAvatarTool(),
                new ReadAnimatorStateTool(),
                new SetAnimatorParameterTool(),
                new PlayAnimatorStateTool(),
                new CreateAnimatorControllerTool(),
                new AddAnimatorStateTool(),
                new AddAnimatorTransitionTool(),
                new AddAnimatorParameterTool(),
                new AssignAnimatorControllerTool(),
                new AddAnimatorLayerTool(),
                new CreateBlendTreeTool(),
                new AddBlendTreeMotionTool(),
                new AddAnimationEventTool(),
                new CreateAnimationClipAssetTool(),
                new SetAnimationFloatCurveTool(),
                new CreateAvatarMaskTool(),
            };

            var registry = new McpToolRegistry();
            Assert.DoesNotThrow(() => registry.RegisterRange(tools));
            Assert.That(registry.Count, Is.EqualTo(19));
            foreach (var tool in tools)
            {
                Assert.That(registry.TryGet(tool.Name, out var registered), Is.True, tool.Name);
                Assert.That(registered, Is.SameAs(tool));
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var schemaError), Is.True, $"{tool.Name}: {schemaError}");
            }

            foreach (var runtimeTool in new IMcpTool[] { new SetAnimatorParameterTool(), new PlayAnimatorStateTool() })
            {
                var descriptor = runtimeTool.Descriptor;
                Assert.That(descriptor.Capabilities, Is.EqualTo(McpCapability.RuntimeCommand), runtimeTool.Name);
                Assert.That(descriptor.EditorMode, Is.EqualTo(McpEditorMode.PlayModeOnly), runtimeTool.Name);
                Assert.That(HttpMcpServer.RequiresLegacyMutationFinalization(descriptor), Is.False, runtimeTool.Name);
            }

            var readStateDescriptor = ((IMcpTool)new ReadAnimatorStateTool()).Descriptor;
            Assert.That(readStateDescriptor.Capabilities, Is.EqualTo(McpCapability.Read));
            Assert.That(readStateDescriptor.EditorMode, Is.EqualTo(McpEditorMode.PlayModeOnly));

            foreach (var assetTool in new IMcpTool[]
            {
                new CreateAnimatorControllerTool(), new AddAnimatorStateTool(), new AddAnimatorTransitionTool(),
                new AddAnimatorParameterTool(), new AddAnimatorLayerTool(), new CreateBlendTreeTool(),
                new AddBlendTreeMotionTool(), new AddAnimationEventTool(), new CreateAnimationClipAssetTool(),
                new SetAnimationFloatCurveTool(), new CreateAvatarMaskTool(),
            })
            {
                var descriptor = assetTool.Descriptor;
                Assert.That((descriptor.Capabilities & McpCapability.AssetWrite), Is.Not.Zero, assetTool.Name);
                Assert.That((descriptor.Capabilities & McpCapability.SceneWrite), Is.EqualTo(McpCapability.None), assetTool.Name);
                Assert.That(descriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly), assetTool.Name);
            }

            var assignDescriptor = ((IMcpTool)new AssignAnimatorControllerTool()).Descriptor;
            Assert.That(assignDescriptor.Capabilities, Is.EqualTo(McpCapability.SceneWrite));
            Assert.That(assignDescriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly));

            AssertControllerReferenceSchema(new ReadAnimatorControllerTool(), new JObject());
            AssertControllerReferenceSchema(new AddAnimatorStateTool(), new JObject { ["state"] = "Idle" });
            AssertControllerReferenceSchema(new AddAnimatorTransitionTool(), new JObject { ["to"] = "Idle" });
            AssertControllerReferenceSchema(new AddAnimatorParameterTool(), new JObject { ["name"] = "Speed" });
            AssertControllerReferenceSchema(new AddAnimatorLayerTool(), new JObject());
            AssertControllerReferenceSchema(new CreateBlendTreeTool(), new JObject
            {
                ["state"] = "Locomotion",
                ["blendParameter"] = "Speed",
            });
            AssertControllerReferenceSchema(new AddBlendTreeMotionTool(), new JObject
            {
                ["state"] = "Locomotion",
                ["clipGuidOrPath"] = ClipPath,
                ["threshold"] = 0f,
            });

            var setParameter = new SetAnimatorParameterTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1, ["name"] = "Speed", ["type"] = "float",
            }, setParameter.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1, ["name"] = "Speed", ["value"] = 1f,
            }, setParameter.InputSchema, out var inferredTypeError), Is.True, inferredTypeError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1, ["name"] = "Jump", ["type"] = "trigger",
            }, setParameter.InputSchema, out var triggerError), Is.True, triggerError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1, ["name"] = "Jump", ["type"] = "trigger", ["value"] = false,
            }, setParameter.InputSchema, out var resetTriggerError), Is.True, resetTriggerError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1, ["name"] = "Mode", ["type"] = "int", ["value"] = 1.5f,
            }, setParameter.InputSchema, out _), Is.False);

            var addParameter = new AddAnimatorParameterTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Mode",
                ["type"] = "Int",
                ["defaultValue"] = 3,
            }, addParameter.InputSchema, out var addParameterError), Is.True, addParameterError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Jump",
                ["type"] = "Trigger",
                ["defaultValue"] = true,
            }, addParameter.InputSchema, out _), Is.False);

            var createBlendTree = new CreateBlendTreeTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "MissingParameter",
                ["blendType"] = "Simple1D",
            }, createBlendTree.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Locomotion2D",
                ["blendType"] = "FreeformCartesian2D",
                ["blendParameter"] = "X",
                ["blendParameterY"] = "Y",
            }, createBlendTree.InputSchema, out var blend2DError), Is.True, blend2DError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Direct",
                ["blendType"] = "Direct",
                ["blendParameter"] = "Weight",
            }, createBlendTree.InputSchema, out _), Is.False);

            var transition = new AddAnimatorTransitionTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject()),
            }, transition.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["to"] = "Run",
                ["duration"] = -0.1f,
            }, transition.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Mode", ["mode"] = "Equals", ["threshold"] = 1.5f,
                }),
            }, transition.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Mode", ["mode"] = "Equals", ["threshold"] = 1,
                }),
            }, transition.InputSchema, out var integerConditionError), Is.True, integerConditionError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Speed", ["mode"] = "Greater", ["threshold"] = 1.5f,
                }),
            }, transition.InputSchema, out var floatConditionError), Is.True, floatConditionError);

            var layer = new AddAnimatorLayerTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath, ["weight"] = 1.1f,
            }, layer.InputSchema, out _), Is.False);

            var motion = new AddBlendTreeMotionTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Locomotion",
                ["clipGuidOrPath"] = ClipPath,
                ["threshold"] = 0f,
                ["position"] = new JArray(0f, 0f),
            }, motion.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Direct",
                ["clipGuidOrPath"] = ClipPath,
                ["directBlendParameter"] = "Weight",
            }, motion.InputSchema, out var directError), Is.True, directError);

            var createMask = new CreateAvatarMaskTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["assetPath"] = Root + "/Invalid.mask",
                ["activeBodyParts"] = new JArray("NotABodyPart"),
            }, createMask.InputSchema, out _), Is.False);
        }

        [Test]
        public void DiscoveryAndReads_ReturnStructuredDeterministicData()
        {
            _controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            var idle = _controller.layers[0].stateMachine.AddState("Idle");
            idle.motion = _clip;
            AnimationUtility.SetAnimationEvents(_clip, new[]
            {
                new AnimationEvent { time = 0.5f, functionName = "Footstep" },
            });
            // The asset-read tools reload from disk; persist the fixture before
            // discovery can trigger pending imports of its newly created assets.
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssets();

            var listed = new ListAnimatorControllersTool().Execute(new JObject
            {
                ["folder"] = Root,
                ["nameContains"] = "Audit",
                ["limit"] = 10,
            });
            Assert.That(listed.IsError, Is.False, listed.Text);
            Assert.That((int)listed.Json["total"], Is.EqualTo(1));
            Assert.That((int)listed.Json["count"], Is.EqualTo(1));
            Assert.That((string)listed.Json["controllers"][0]["assetPath"], Is.EqualTo(ControllerPath));

            var controller = new ReadAnimatorControllerTool().Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
            });
            Assert.That(controller.IsError, Is.False, controller.Text);
            Assert.That((string)controller.Json["assetPath"], Is.EqualTo(ControllerPath));
            Assert.That(((JArray)controller.Json["parameters"]).Any(item => (string)item["name"] == "Speed"), Is.True);
            Assert.That(((JArray)controller.Json["layers"])[0]["states"].Any(item => (string)item["name"] == "Idle"), Is.True);

            var clip = new ReadAnimationClipTool().Execute(new JObject { ["clipGuidOrPath"] = ClipPath });
            Assert.That(clip.IsError, Is.False, clip.Text);
            Assert.That((string)clip.Json["assetPath"], Is.EqualTo(ClipPath));
            Assert.That((int)clip.Json["curveCount"], Is.EqualTo(1));
            Assert.That((int)clip.Json["eventCount"], Is.EqualTo(1));
            Assert.That((string)clip.Json["events"][0]["functionName"], Is.EqualTo("Footstep"));

            var avatar = new ReadAvatarTool().Execute(new JObject { ["guidOrPath"] = AvatarPath });
            Assert.That(avatar.IsError, Is.False, avatar.Text);
            Assert.That((string)avatar.Json["assetPath"], Is.EqualTo(AvatarPath));
            Assert.That(avatar.Json["isValid"], Is.Not.Null);
        }

        [Test]
        public void CreateController_RejectsUnsafeAndConflictingPathsBeforeCreatingAsset()
        {
            var tool = new CreateAnimatorControllerTool();
            ToolResult unsafeResult = null;
            Assert.DoesNotThrow(() => unsafeResult = tool.Execute(new JObject { ["path"] = "../Outside.controller" }));
            Assert.That(unsafeResult.IsError, Is.True);

            ToolResult conflict = null;
            Assert.DoesNotThrow(() => conflict = tool.Execute(new JObject { ["path"] = ControllerPath }));
            Assert.That(conflict.IsError, Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath), Is.SameAs(_controller));

            var path = Root + "/Created.controller";
            var created = tool.Execute(new JObject { ["path"] = path });
            Assert.That(created.IsError, Is.False, created.Text);
            Assert.That((string)created.Json["controller"]?["assetPath"], Is.EqualTo(path));
            Assert.That((bool)created.Json["assetCreated"], Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(path), Is.Not.Null);
        }

        [Test]
        public void AddState_RejectsBadClipDuplicateAndLayerBeforeMutationAndUndoRestoresValidState()
        {
            var tool = new AddAnimatorStateTool();
            var stateMachine = _controller.layers[0].stateMachine;
            var before = stateMachine.states.Length;

            var missingClip = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Rejected",
                ["clipGuidOrPath"] = Root + "/Missing.anim",
            });
            Assert.That(missingClip.IsError, Is.True);
            Assert.That(stateMachine.states, Has.Length.EqualTo(before));

            ToolResult negativeLayer = null;
            Assert.DoesNotThrow(() => negativeLayer = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Negative",
                ["layer"] = -1,
            }));
            Assert.That(negativeLayer.IsError, Is.True);
            Assert.That(stateMachine.states, Has.Length.EqualTo(before));

            stateMachine.AddState("Existing");
            var duplicate = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Existing",
            });
            Assert.That(duplicate.IsError, Is.True);
            Assert.That(stateMachine.states.Count(child => child.state.name == "Existing"), Is.EqualTo(1));

            Undo.ClearAll();
            var added = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Walk",
                ["clipGuidOrPath"] = ClipPath,
                ["speed"] = 1.25f,
                ["isDefault"] = true,
            });
            Assert.That(added.IsError, Is.False, added.Text);
            var state = stateMachine.states.Single(child => child.state.name == "Walk").state;
            Assert.That(AssetDatabase.GetAssetPath(state.motion), Is.EqualTo(ClipPath));
            Assert.That(state.speed, Is.EqualTo(1.25f));
            Assert.That(stateMachine.defaultState, Is.SameAs(state));
            Assert.That((string)added.Json["state"]?["name"], Is.EqualTo("Walk"));

            Undo.PerformUndo();
            Assert.That(stateMachine.states.Any(child => child.state != null && child.state.name == "Walk"), Is.False);
        }

        [Test]
        public void AddParameter_ValidatesTypedDefaultAndDuplicatesBeforeMutationAndSupportsUndo()
        {
            var tool = new AddAnimatorParameterTool();
            var before = _controller.parameters.Length;
            ToolResult badDefault = null;
            Assert.DoesNotThrow(() => badDefault = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Mode",
                ["type"] = "Int",
                ["defaultValue"] = "not-an-int",
            }));
            Assert.That(badDefault.IsError, Is.True);
            Assert.That(_controller.parameters, Has.Length.EqualTo(before));

            _controller.AddParameter("Existing", AnimatorControllerParameterType.Bool);
            var duplicate = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Existing",
                ["type"] = "Bool",
            });
            Assert.That(duplicate.IsError, Is.True);
            Assert.That(_controller.parameters.Count(parameter => parameter.name == "Existing"), Is.EqualTo(1));

            Undo.ClearAll();
            var added = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Mode",
                ["type"] = "Int",
                ["defaultValue"] = 3,
            });
            Assert.That(added.IsError, Is.False, added.Text);
            var parameter = _controller.parameters.Single(value => value.name == "Mode");
            Assert.That(parameter.type, Is.EqualTo(AnimatorControllerParameterType.Int));
            Assert.That(parameter.defaultInt, Is.EqualTo(3));
            Assert.That((int)added.Json["parameter"]?["defaultValue"], Is.EqualTo(3));

            Undo.PerformUndo();
            Assert.That(_controller.parameters.Any(value => value.name == "Mode"), Is.False);
        }

        [Test]
        public void AddTransition_ValidatesConditionsBeforeCreationAndReturnsEffectiveTransition()
        {
            var stateMachine = _controller.layers[0].stateMachine;
            var idle = stateMachine.AddState("Idle");
            var run = stateMachine.AddState("Run");
            _controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            _controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            var tool = new AddAnimatorTransitionTool();

            var incompatible = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["from"] = "Idle",
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Grounded",
                    ["mode"] = "Greater",
                    ["threshold"] = 0f,
                }),
            });
            Assert.That(incompatible.IsError, Is.True);
            Assert.That(idle.transitions, Is.Empty);

            var missingParameter = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["from"] = "Idle",
                ["to"] = "Run",
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Missing",
                    ["mode"] = "Greater",
                    ["threshold"] = 1f,
                }),
            });
            Assert.That(missingParameter.IsError, Is.True);
            Assert.That(idle.transitions, Is.Empty);

            Undo.ClearAll();
            var added = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["from"] = "Idle",
                ["to"] = "Run",
                ["hasExitTime"] = true,
                ["exitTime"] = 0.8f,
                ["duration"] = 0.2f,
                ["fixedDuration"] = true,
                ["offset"] = 0.1f,
                ["conditions"] = new JArray(new JObject
                {
                    ["parameter"] = "Speed",
                    ["mode"] = "Greater",
                    ["threshold"] = 1f,
                }),
            });
            Assert.That(added.IsError, Is.False, added.Text);
            var transition = idle.transitions.Single();
            Assert.That(transition.destinationState, Is.SameAs(run));
            Assert.That(transition.hasExitTime, Is.True);
            Assert.That(transition.exitTime, Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(transition.duration, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(transition.hasFixedDuration, Is.True);
            Assert.That(transition.offset, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(transition.conditions, Has.Length.EqualTo(1));
            Assert.That((string)added.Json["transition"]?["to"], Is.EqualTo("Run"));

            Undo.PerformUndo();
            Assert.That(idle.transitions, Is.Empty);
        }

        [Test]
        public void AddLayer_RejectsBadMaskBeforeMutationAndUndoRestoresValidLayer()
        {
            var tool = new AddAnimatorLayerTool();
            var before = _controller.layers.Length;
            var rejected = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "Rejected",
                ["avatarMaskGuidOrPath"] = Root + "/Missing.mask",
            });
            Assert.That(rejected.IsError, Is.True);
            Assert.That(_controller.layers, Has.Length.EqualTo(before));

            Undo.ClearAll();
            var added = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["name"] = "UpperBody",
                ["weight"] = 0.5f,
                ["blendingMode"] = "Additive",
                ["avatarMaskGuidOrPath"] = MaskPath,
            });
            Assert.That(added.IsError, Is.False, added.Text);
            var layer = _controller.layers.Single(value => value.name == "UpperBody");
            Assert.That(layer.stateMachine, Is.Not.Null);
            Assert.That(layer.defaultWeight, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Additive));
            Assert.That(layer.avatarMask, Is.SameAs(_mask));
            Assert.That((string)added.Json["layer"]?["name"], Is.EqualTo("UpperBody"));

            Undo.PerformUndo();
            Assert.That(_controller.layers.Any(value => value.name == "UpperBody"), Is.False);
        }

        [Test]
        public void CreateBlendTree_ValidatesLayerTypeAndParametersBeforeCreatingSubAssets()
        {
            _controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            var stateMachine = _controller.layers[0].stateMachine;
            var before = stateMachine.states.Length;
            var tool = new CreateBlendTreeTool();

            ToolResult badLayer = null;
            Assert.DoesNotThrow(() => badLayer = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "BadLayer",
                ["layer"] = -1,
                ["blendType"] = "Simple1D",
                ["blendParameter"] = "Speed",
            }));
            Assert.That(badLayer.IsError, Is.True);
            Assert.That(stateMachine.states, Has.Length.EqualTo(before));

            var missingParameter = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "MissingParameter",
                ["blendType"] = "Simple1D",
                ["blendParameter"] = "Missing",
            });
            Assert.That(missingParameter.IsError, Is.True);
            Assert.That(stateMachine.states, Has.Length.EqualTo(before));

            Undo.ClearAll();
            var created = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Locomotion",
                ["blendType"] = "Simple1D",
                ["blendParameter"] = "Speed",
            });
            Assert.That(created.IsError, Is.False, created.Text);
            var state = stateMachine.states.Single(child => child.state.name == "Locomotion").state;
            var tree = state.motion as BlendTree;
            Assert.That(tree, Is.Not.Null);
            Assert.That(tree.blendType, Is.EqualTo(BlendTreeType.Simple1D));
            Assert.That(tree.blendParameter, Is.EqualTo("Speed"));
            Assert.That((string)created.Json["blendTree"]?["state"], Is.EqualTo("Locomotion"));

            Undo.PerformUndo();
            Assert.That(stateMachine.states.Any(child => child.state != null && child.state.name == "Locomotion"), Is.False);
        }

        [Test]
        public void AddBlendTreeMotion_RequiresShapeMatchingTreeAndSupportsDirectParameters()
        {
            _controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            _controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            _controller.CreateBlendTreeInController("OneD", out var oneD, 0);
            oneD.blendType = BlendTreeType.Simple1D;
            oneD.blendParameter = "Speed";
            _controller.CreateBlendTreeInController("Direct", out var direct, 0);
            direct.blendType = BlendTreeType.Direct;
            var tool = new AddBlendTreeMotionTool();

            var wrongShape = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "OneD",
                ["clipGuidOrPath"] = ClipPath,
                ["position"] = new JArray(0f, 0f),
            });
            Assert.That(wrongShape.IsError, Is.True);
            Assert.That(oneD.children, Is.Empty);

            var threshold = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "OneD",
                ["clipGuidOrPath"] = ClipPath,
                ["threshold"] = 0.5f,
            });
            Assert.That(threshold.IsError, Is.False, threshold.Text);
            Assert.That(oneD.children, Has.Length.EqualTo(1));
            Assert.That(oneD.children[0].threshold, Is.EqualTo(0.5f).Within(0.0001f));

            var directResult = tool.Execute(new JObject
            {
                ["controllerGuidOrPath"] = ControllerPath,
                ["state"] = "Direct",
                ["clipGuidOrPath"] = ClipPath,
                ["directBlendParameter"] = "Weight",
            });
            Assert.That(directResult.IsError, Is.False, directResult.Text);
            Assert.That(direct.children, Has.Length.EqualTo(1));
            Assert.That(direct.children[0].directBlendParameter, Is.EqualTo("Weight"));
            Assert.That((string)directResult.Json["child"]?["directBlendParameter"], Is.EqualTo("Weight"));
        }

        [Test]
        public void AnimationEventAndCurve_ValidateBeforeMutationReturnBindingsAndSupportUndo()
        {
            var eventTool = new AddAnimationEventTool();
            var beforeEvents = AnimationUtility.GetAnimationEvents(_clip).Length;
            var badTime = eventTool.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["time"] = 2f,
                ["functionName"] = "TooLate",
            });
            Assert.That(badTime.IsError, Is.True);
            Assert.That(AnimationUtility.GetAnimationEvents(_clip), Has.Length.EqualTo(beforeEvents));

            Undo.ClearAll();
            var added = eventTool.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["time"] = 0.5f,
                ["functionName"] = "Footstep",
                ["stringParameter"] = "left",
            });
            Assert.That(added.IsError, Is.False, added.Text);
            Assert.That(AnimationUtility.GetAnimationEvents(_clip), Has.Length.EqualTo(beforeEvents + 1));
            Assert.That((string)added.Json["event"]?["functionName"], Is.EqualTo("Footstep"));
            Undo.PerformUndo();
            Assert.That(AnimationUtility.GetAnimationEvents(_clip), Has.Length.EqualTo(beforeEvents));

            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.y");
            var curveTool = new SetAnimationFloatCurveTool();
            ToolResult invalid = null;
            Assert.DoesNotThrow(() => invalid = curveTool.Execute(new JObject
            {
                ["clipPath"] = ClipPath,
                ["relativePath"] = string.Empty,
                ["componentType"] = typeof(Transform).FullName,
                ["propertyName"] = "m_LocalPosition.y",
                ["keys"] = new JArray(new JObject { ["time"] = double.NaN, ["value"] = 1f }),
            }));
            Assert.That(invalid.IsError, Is.True);
            Assert.That(AnimationUtility.GetEditorCurve(_clip, binding), Is.Null);

            Undo.ClearAll();
            var changed = curveTool.Execute(new JObject
            {
                ["clipPath"] = ClipPath,
                ["relativePath"] = string.Empty,
                ["componentType"] = typeof(Transform).FullName,
                ["propertyName"] = "m_LocalPosition.y",
                ["keys"] = new JArray(
                    new JObject { ["time"] = 0f, ["value"] = 0f },
                    new JObject { ["time"] = 1f, ["value"] = 2f }),
            });
            Assert.That(changed.IsError, Is.False, changed.Text);
            Assert.That(AnimationUtility.GetEditorCurve(_clip, binding), Is.Not.Null);
            Assert.That((string)changed.Json["binding"]?["propertyName"], Is.EqualTo("m_LocalPosition.y"));
            Undo.PerformUndo();
            Assert.That(AnimationUtility.GetEditorCurve(_clip, binding), Is.Null);
        }

        [Test]
        public void CreateClipAndAvatarMask_RejectInvalidInputsBeforeAssetCreationAndReturnEffectiveState()
        {
            var clipTool = new CreateAnimationClipAssetTool();
            var invalidClipPath = Root + "/Invalid.anim";
            var badRate = clipTool.Execute(new JObject
            {
                ["assetPath"] = invalidClipPath,
                ["frameRate"] = 0f,
                ["wrapMode"] = "Loop",
            });
            Assert.That(badRate.IsError, Is.True);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(invalidClipPath), Is.Null);

            var createdClipPath = Root + "/Created.anim";
            var createdClip = clipTool.Execute(new JObject
            {
                ["assetPath"] = createdClipPath,
                ["frameRate"] = 30f,
                ["wrapMode"] = "Loop",
            });
            Assert.That(createdClip.IsError, Is.False, createdClip.Text);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(createdClipPath);
            Assert.That(clip, Is.Not.Null);
            Assert.That(clip.frameRate, Is.EqualTo(30f));
            Assert.That(clip.wrapMode, Is.EqualTo(WrapMode.Loop));
            Assert.That((string)createdClip.Json["clip"]?["assetPath"], Is.EqualTo(createdClipPath));
            Assert.That((bool)createdClip.Json["assetCreated"], Is.True);

            var maskTool = new CreateAvatarMaskTool();
            var invalidMaskPath = Root + "/Invalid.mask";
            var invalidMask = maskTool.Execute(new JObject
            {
                ["assetPath"] = invalidMaskPath,
                ["activeBodyParts"] = new JArray("NotABodyPart"),
            });
            Assert.That(invalidMask.IsError, Is.True);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(invalidMaskPath), Is.Null);

            var createdMaskPath = Root + "/Created.mask";
            var createdMask = maskTool.Execute(new JObject
            {
                ["assetPath"] = createdMaskPath,
                ["activeBodyParts"] = new JArray("Head", "LeftArm"),
            });
            Assert.That(createdMask.IsError, Is.False, createdMask.Text);
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(createdMaskPath);
            Assert.That(mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Head), Is.True);
            Assert.That(mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm), Is.True);
            Assert.That(mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm), Is.False);
            Assert.That(((JArray)createdMask.Json["activeBodyParts"]).Values<string>(), Is.EquivalentTo(new[] { "Head", "LeftArm" }));
        }

        [Test]
        public void AssignController_RecordsPrefabOverrideReturnsStateAndUndoRestoresAssignment()
        {
            var source = new GameObject("Animated");
            source.AddComponent<Animator>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
            UnityEngine.Object.DestroyImmediate(source);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var animator = instance.GetComponent<Animator>();
            Assert.That(animator.runtimeAnimatorController, Is.Null);

            Undo.ClearAll();
            var assigned = new AssignAnimatorControllerTool().Execute(new JObject
            {
                ["instanceId"] = instance.GetMcpInstanceId(),
                ["controllerGuidOrPath"] = ControllerPath,
            });
            Assert.That(assigned.IsError, Is.False, assigned.Text);
            Assert.That(animator.runtimeAnimatorController, Is.SameAs(_controller));
            Assert.That((string)assigned.Json["controller"]?["assetPath"], Is.EqualTo(ControllerPath));
            var modifications = PrefabUtility.GetPropertyModifications(instance) ?? Array.Empty<PropertyModification>();
            Assert.That(modifications.Any(modification => modification.propertyPath == "m_Controller" && modification.objectReference == _controller), Is.True);

            Undo.PerformUndo();
            Assert.That(animator.runtimeAnimatorController, Is.Null);
        }

        [Test]
        public void RuntimeAnimatorTools_FailClosedInEditModeWithStructuredCodes()
        {
            _controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            _controller.layers[0].stateMachine.AddState("Idle");
            var gameObject = new GameObject("RuntimeAnimator");
            var animator = gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = _controller;

            var set = new SetAnimatorParameterTool().Execute(new JObject
            {
                ["instanceId"] = gameObject.GetMcpInstanceId(),
                ["name"] = "Speed",
                ["type"] = "auto",
                ["value"] = 1f,
            });
            Assert.That(set.IsError, Is.True);
            Assert.That((string)set.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));

            var play = new PlayAnimatorStateTool().Execute(new JObject
            {
                ["instanceId"] = gameObject.GetMcpInstanceId(),
                ["state"] = "Base Layer.Idle",
                ["layer"] = 0,
                ["normalizedTime"] = 0f,
            });
            Assert.That(play.IsError, Is.True);
            Assert.That((string)play.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));

            var read = new ReadAnimatorStateTool().Execute(new JObject
            {
                ["instanceId"] = gameObject.GetMcpInstanceId(),
            });
            Assert.That(read.IsError, Is.True);
            Assert.That((string)read.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));
        }

        private static void AssertControllerReferenceSchema(IMcpTool tool, JObject requiredArguments)
        {
            var none = (JObject)requiredArguments.DeepClone();
            Assert.That(ToolSchemaValidator.ValidateArguments(none, tool.InputSchema, out _), Is.False, tool.Name + " without controller reference");

            var both = (JObject)requiredArguments.DeepClone();
            both["controllerGuidOrPath"] = ControllerPath;
            both["controllerInstanceId"] = 1;
            Assert.That(ToolSchemaValidator.ValidateArguments(both, tool.InputSchema, out _), Is.False, tool.Name + " with two controller references");

            var byPath = (JObject)requiredArguments.DeepClone();
            byPath["controllerGuidOrPath"] = ControllerPath;
            Assert.That(ToolSchemaValidator.ValidateArguments(byPath, tool.InputSchema, out var error), Is.True, $"{tool.Name}: {error}");
        }
    }
}
