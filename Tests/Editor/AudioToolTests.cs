using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Audio;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class AudioToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerAudioAudit";
        private const string ClipPath = Root + "/Tone.wav";
        private const string MixerAPath = Root + "/AuditA.mixer";
        private const string MixerBPath = Root + "/AuditB.mixer";
        private const string TwinMixerAPath = Root + "/TwinA.mixer";
        private const string TwinMixerBPath = Root + "/TwinB.mixer";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerAudioAudit");
            WritePcmWave(ClipPath);
            AssetDatabase.ImportAsset(ClipPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath), Is.Not.Null);
            Assert.That(CreateMixer(MixerAPath), Is.Not.Null);
            Assert.That(CreateMixer(MixerBPath), Is.Not.Null);
            var twinMixerA = CreateMixer(TwinMixerAPath);
            var twinMixerB = CreateMixer(TwinMixerBPath);
            Assert.That(twinMixerA, Is.Not.Null);
            Assert.That(twinMixerB, Is.Not.Null);
            twinMixerA.name = "Shared";
            twinMixerB.name = "Shared";
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void Schemas_ExposeDocumentedBoundsReferencesAndRuntimeModes()
        {
            var simple = new CreateAudioSourceTool();
            var create3D = new CreateAudioSource3DTool();
            var setProps = new SetAudioSourcePropsTool();
            var readMixer = new ReadAudioMixerTool();
            IMcpTool[] tools =
            {
                simple,
                create3D,
                setProps,
                new PlayAudioSourceTool(),
                new AddAudioListenerTool(),
                new CreateReverbZoneTool(),
                new SetupBackgroundMusicTool(),
                new ListAudioMixersTool(),
                readMixer,
                new InspectAudioSetupTool(),
                new AssignToMixerGroupTool(),
                new SetMixerParameterTool(),
                new PlayMixerSnapshotTool(),
            };
            var registry = new McpToolRegistry();
            Assert.DoesNotThrow(() => registry.RegisterRange(tools));
            Assert.That(registry.Count, Is.EqualTo(13));
            foreach (var tool in tools)
            {
                Assert.That(registry.TryGet(tool.Name, out var registered), Is.True, tool.Name);
                Assert.That(registered, Is.SameAs(tool));
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            }

            Assert.That(simple.InputSchema["properties"]?["clipGuidOrPath"], Is.Not.Null);
            Assert.That((float)simple.InputSchema["properties"]?["pitch"]?["minimum"], Is.EqualTo(-3f));
            Assert.That((float)simple.InputSchema["properties"]?["pitch"]?["maximum"], Is.EqualTo(3f));
            Assert.That((float)create3D.InputSchema["properties"]?["volume"]?["minimum"], Is.Zero);
            Assert.That((float)create3D.InputSchema["properties"]?["volume"]?["maximum"], Is.EqualTo(1f));
            Assert.That((float)create3D.InputSchema["properties"]?["reverbZoneMix"]?["maximum"], Is.EqualTo(1.1f));
            Assert.That((int)create3D.InputSchema["properties"]?["spread"]?["maximum"], Is.EqualTo(360));
            Assert.That((int)setProps.InputSchema["properties"]?["priority"]?["maximum"], Is.EqualTo(256));
            Assert.That(readMixer.InputSchema["oneOf"], Is.Not.Null);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["position"] = new JObject { ["x"] = 1f, ["y"] = 2f } }, simple.InputSchema, out _),
                Is.False, "Vector3 object schemas must require x, y and z");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["position"] = new JObject { ["x"] = 1f, ["y"] = 2f, ["z"] = 3f } }, simple.InputSchema, out var vectorError),
                Is.True, vectorError);

            var playTool = new PlayAudioSourceTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["instanceId"] = 1, ["action"] = "play" }, playTool.InputSchema, out var playError),
                Is.True, playError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["instanceId"] = 1, ["action"] = "playOneShot" }, playTool.InputSchema, out _),
                Is.False, "playOneShot must require clip at schema validation time");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["instanceId"] = 1, ["action"] = "playOneShot", ["clip"] = ClipPath }, playTool.InputSchema, out var oneShotError),
                Is.True, oneShotError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["instanceId"] = 1, ["action"] = "play", ["clip"] = ClipPath }, playTool.InputSchema, out _),
                Is.False, "clip and volumeScale are only meaningful for playOneShot");

            var runtimeTools = new IMcpTool[]
            {
                playTool,
                new SetMixerParameterTool(),
                new PlayMixerSnapshotTool(),
            };
            foreach (var runtimeTool in runtimeTools)
            {
                var descriptor = runtimeTool.Descriptor;
                Assert.That(descriptor.EditorMode, Is.EqualTo(McpEditorMode.PlayModeOnly), runtimeTool.Name);
                Assert.That(descriptor.Capabilities, Is.EqualTo(McpCapability.RuntimeCommand), runtimeTool.Name);
                Assert.That((descriptor.Capabilities & McpCapability.SceneWrite), Is.EqualTo(McpCapability.None), runtimeTool.Name);
                Assert.That(McpRequestProcessor.RequiresLegacyMutationFinalization(descriptor), Is.False, runtimeTool.Name);
            }

            var listenerTool = new AddAudioListenerTool();
            var listenerDescriptor = ((IMcpTool)listenerTool).Descriptor;
            Assert.That((listenerDescriptor.Capabilities & McpCapability.Destructive), Is.Not.Zero);
            Assert.That(listenerDescriptor.Risk, Is.EqualTo(McpRiskLevel.High));
            Assert.That(listenerDescriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));
            var allowWrites = Maicon.SceneAnalyzer.Editor.Settings.AllowWrites;
            var allowDestructive = Maicon.SceneAnalyzer.Editor.Settings.AllowDestructiveTools;
            try
            {
                Maicon.SceneAnalyzer.Editor.Settings.AllowWrites = true;
                Maicon.SceneAnalyzer.Editor.Settings.AllowDestructiveTools = false;
                Assert.That(ToolAuthorization.TryAuthorize(listenerTool, listenerDescriptor, out var deniedCode, out _), Is.False);
                Assert.That(deniedCode, Is.EqualTo("CAPABILITY_DISABLED"));
            }
            finally
            {
                Maicon.SceneAnalyzer.Editor.Settings.AllowWrites = allowWrites;
                Maicon.SceneAnalyzer.Editor.Settings.AllowDestructiveTools = allowDestructive;
            }
        }

        [Test]
        public void CreateAudioSource_RejectsBadReferencesBeforeCreationAndReturnsEffectiveState()
        {
            var before = Count<AudioSource>();
            var tool = new CreateAudioSourceTool();
            var rejected = tool.Execute(new JObject
            {
                ["name"] = "Rejected",
                ["clipGuidOrPath"] = Root + "/Missing.wav",
            });
            Assert.That(rejected.IsError, Is.True);
            Assert.That(Count<AudioSource>(), Is.EqualTo(before));

            var badParent = tool.Execute(new JObject
            {
                ["name"] = "BadParent",
                ["clipGuidOrPath"] = ClipPath,
                ["parent"] = int.MaxValue,
            });
            Assert.That(badParent.IsError, Is.True);
            Assert.That(Count<AudioSource>(), Is.EqualTo(before));

            var created = tool.Execute(new JObject
            {
                ["name"] = "SimpleAudio",
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.4f,
                ["pitch"] = 1.25f,
                ["spatial"] = true,
                ["position"] = new JArray(1f, 2f, 3f),
            });
            Assert.That(created.IsError, Is.False, created.Text);
            var go = UnityObjectIds.Resolve((int)created.Json["instanceId"]) as GameObject;
            Assert.That(go, Is.Not.Null);
            var source = go.GetComponent<AudioSource>();
            Assert.That(source.clip, Is.SameAs(AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath)));
            Assert.That(source.volume, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(source.pitch, Is.EqualTo(1.25f).Within(0.0001f));
            Assert.That(source.spatialBlend, Is.EqualTo(1f));
            Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(created.Json["source"], Is.Not.Null);
            Assert.That((string)created.Json["clip"]?["assetPath"], Is.EqualTo(ClipPath));
            Assert.That(go.scene.isDirty, Is.True);

            Undo.PerformUndo();
            Assert.That(go == null, Is.True);
        }

        [Test]
        public void Create3DAndSetProps_AreTransactionalStructuredAndUndoable()
        {
            var go = new GameObject("Emitter");
            var source = go.AddComponent<AudioSource>();
            source.volume = 0.2f;
            source.spatialBlend = 0.25f;
            source.minDistance = 2f;
            source.maxDistance = 30f;

            var create3D = new CreateAudioSource3DTool();
            var rejected = create3D.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["clipGuidOrPath"] = Root + "/Missing.wav",
                ["volume"] = 0.9f,
                ["minDistance"] = 4f,
                ["maxDistance"] = 40f,
            });
            Assert.That(rejected.IsError, Is.True);
            Assert.That(source.volume, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(source.spatialBlend, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(source.minDistance, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(source.maxDistance, Is.EqualTo(30f).Within(0.0001f));

            var configured = create3D.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.6f,
                ["pitch"] = 1.1f,
                ["minDistance"] = 3f,
                ["maxDistance"] = 60f,
                ["spread"] = 90f,
                ["priority"] = 32,
                ["reverbZoneMix"] = 1.05f,
                ["mixerGroup"] = "Master",
                ["mixerGuidOrPath"] = MixerAPath,
            });
            Assert.That(configured.IsError, Is.False, configured.Text);
            Assert.That(source.clip, Is.Not.Null);
            Assert.That(source.outputAudioMixerGroup, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(source.outputAudioMixerGroup.audioMixer), Is.EqualTo(MixerAPath));
            Assert.That((float)configured.Json["sourceState"]?["maxDistance"], Is.EqualTo(60f));

            Undo.PerformUndo();
            Assert.That(source.clip, Is.Null);
            Assert.That(source.volume, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(source.spatialBlend, Is.EqualTo(0.25f).Within(0.0001f));

            Undo.ClearAll();
            var setProps = new SetAudioSourcePropsTool();
            var badPatch = setProps.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["volume"] = 0.8f,
                ["clip"] = Root + "/Missing.wav",
            });
            Assert.That(badPatch.IsError, Is.True);
            Assert.That(source.volume, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(source.clip, Is.Null);

            var badDistances = setProps.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["minDistance"] = 20f,
                ["maxDistance"] = 10f,
            });
            Assert.That(badDistances.IsError, Is.True);
            Assert.That(source.minDistance, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(source.maxDistance, Is.EqualTo(30f).Within(0.0001f));

            var changed = setProps.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["volume"] = 0.7f,
                ["pitch"] = -1.5f,
                ["clip"] = ClipPath,
                ["rolloffMode"] = "Linear",
            });
            Assert.That(changed.IsError, Is.False, changed.Text);
            Assert.That(((JArray)changed.Json["changed"]).Values<string>(), Does.Contain("volume"));
            Assert.That((float)changed.Json["sourceState"]?["volume"], Is.EqualTo(0.7f));
            Assert.That(source.rolloffMode, Is.EqualTo(AudioRolloffMode.Linear));

            Undo.PerformUndo();
            Assert.That(source.volume, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(source.clip, Is.Null);
        }

        [Test]
        public void BackgroundMusicAndReverb_RejectInvalidInputsBeforeMutation()
        {
            var bgm = new SetupBackgroundMusicTool();
            var badGroup = bgm.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["mixerGroup"] = "MissingGroup",
            });
            Assert.That(badGroup.IsError, Is.True);
            Assert.That(GameObject.Find("__BGM__"), Is.Null);

            var configured = bgm.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.35f,
                ["mixerGroup"] = "Master",
            });
            Assert.That(configured.IsError, Is.True, "The unqualified group must be rejected while two mixers are ambiguous");
            Assert.That(GameObject.Find("__BGM__"), Is.Null);

            configured = bgm.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.35f,
                ["mixerGroup"] = "AuditA/Master",
            });
            Assert.That(configured.IsError, Is.False, configured.Text);
            var bgmGo = GameObject.Find("__BGM__");
            var source = bgmGo.GetComponent<AudioSource>();
            Assert.That(source.outputAudioMixerGroup, Is.Not.Null);
            Assert.That((string)configured.Json["clip"]?["assetPath"], Is.EqualTo(ClipPath));

            bgmGo.SetActive(false);
            var reactivated = bgm.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.4f,
                ["mixerGroup"] = "AuditA/Master",
                ["replaceExisting"] = true,
            });
            Assert.That(reactivated.IsError, Is.False, reactivated.Text);
            Assert.That(bgmGo.activeSelf, Is.True, "setup_background_music must reactivate an inactive singleton so it can emit audio");
            Undo.PerformUndo();
            Assert.That(bgmGo.activeSelf, Is.False, "BGM reactivation must participate in the same Undo group");

            var preserved = bgm.Execute(new JObject
            {
                ["clipGuidOrPath"] = ClipPath,
                ["volume"] = 0.9f,
                ["replaceExisting"] = false,
            });
            Assert.That(preserved.IsError, Is.True);
            Assert.That(source.volume, Is.EqualTo(0.35f).Within(0.0001f));

            var reverb = new CreateReverbZoneTool();
            var before = Count<AudioReverbZone>();
            var badPreset = reverb.Execute(new JObject
            {
                ["preset"] = "NotARealPreset",
            });
            Assert.That(badPreset.IsError, Is.True);
            Assert.That(Count<AudioReverbZone>(), Is.EqualTo(before));

            var badDistances = reverb.Execute(new JObject
            {
                ["minDistance"] = 20f,
                ["maxDistance"] = 10f,
            });
            Assert.That(badDistances.IsError, Is.True);
            Assert.That(Count<AudioReverbZone>(), Is.EqualTo(before));

            var created = reverb.Execute(new JObject
            {
                ["name"] = "CaveZone",
                ["minDistance"] = 5f,
                ["maxDistance"] = 25f,
                ["preset"] = "Cave",
                ["position"] = new JObject { ["x"] = 3f, ["y"] = 4f, ["z"] = 5f },
            });
            Assert.That(created.IsError, Is.False, created.Text);
            Assert.That((string)created.Json["preset"], Is.EqualTo("Cave"));
            Assert.That((float)created.Json["maxDistance"], Is.EqualTo(25f));
        }

        [Test]
        public void ListenerAndInspection_IncludeInactiveObjectsAndReturnStructuredAudit()
        {
            var inactiveListenerGo = new GameObject("InactiveListener");
            var inactiveListener = inactiveListenerGo.AddComponent<AudioListener>();
            inactiveListenerGo.SetActive(false);
            var inactiveSourceGo = new GameObject("InactiveSource");
            inactiveSourceGo.AddComponent<AudioSource>();
            inactiveSourceGo.SetActive(false);

            var audit = new InspectAudioSetupTool().Execute(new JObject());
            Assert.That(audit.IsError, Is.False, audit.Text);
            Assert.That((int)audit.Json["listenerCount"], Is.EqualTo(1));
            Assert.That((int)audit.Json["activeListenerCount"], Is.Zero);
            Assert.That((int)audit.Json["sourceCount"], Is.EqualTo(1));
            Assert.That((JArray)audit.Json["listeners"], Has.Count.EqualTo(1));
            Assert.That((JArray)audit.Json["sources"], Has.Count.EqualTo(1));

            var twinA = AssetDatabase.LoadAssetAtPath<AudioMixer>(TwinMixerAPath);
            var twinB = AssetDatabase.LoadAssetAtPath<AudioMixer>(TwinMixerBPath);
            var routedA = new GameObject("TwinSourceA").AddComponent<AudioSource>();
            var routedB = new GameObject("TwinSourceB").AddComponent<AudioSource>();
            routedA.outputAudioMixerGroup = twinA.FindMatchingGroups("Master").Single();
            routedB.outputAudioMixerGroup = twinB.FindMatchingGroups("Master").Single();
            audit = new InspectAudioSetupTool().Execute(new JObject());
            var routedGroups = ((JArray)audit.Json["byMixerGroup"])
                .Where(item => item["mixer"] is JObject)
                .ToArray();
            Assert.That(routedGroups, Has.Length.EqualTo(2), "homonymous mixer assets must remain separate audit buckets");
            CollectionAssert.AreEquivalent(
                new[] { TwinMixerAPath, TwinMixerBPath },
                routedGroups.Select(item => (string)item["mixer"]?["assetPath"]).ToArray());
            Assert.That(routedGroups.All(item => (int)item["count"] == 1), Is.True);

            var target = new GameObject("ListenerTarget");
            var added = new AddAudioListenerTool().Execute(new JObject
            {
                ["instanceId"] = target.GetMcpInstanceId(),
                ["removeOthers"] = true,
            });
            Assert.That(added.IsError, Is.False, added.Text);
            Assert.That(target.GetComponent<AudioListener>(), Is.Not.Null);
            Assert.That(inactiveListener == null, Is.True);
            Assert.That((int)added.Json["removedCount"], Is.EqualTo(1));

            Undo.PerformUndo();
            Assert.That(target.GetComponent<AudioListener>(), Is.Null);
            Assert.That(inactiveListenerGo.GetComponent<AudioListener>(), Is.Not.Null);
        }

        [Test]
        public void MixerDiscoveryReadAndRouting_AreDeterministicStructuredAndUnambiguous()
        {
            var listed = new ListAudioMixersTool().Execute(new JObject
            {
                ["folder"] = Root,
                ["nameContains"] = "Audit",
                ["limit"] = 10,
            });
            Assert.That(listed.IsError, Is.False, listed.Text);
            var mixers = (JArray)listed.Json["mixers"];
            Assert.That(mixers, Has.Count.EqualTo(2));
            CollectionAssert.AreEqual(new[] { MixerAPath, MixerBPath }, mixers.Select(item => (string)item["assetPath"]).ToArray());
            Assert.That((int)listed.Json["total"], Is.EqualTo(2));

            var read = new ReadAudioMixerTool().Execute(new JObject { ["mixerGuidOrPath"] = MixerAPath });
            Assert.That(read.IsError, Is.False, read.Text);
            Assert.That((string)read.Json["assetPath"], Is.EqualTo(MixerAPath));
            Assert.That(((JArray)read.Json["groups"]).Any(item => (string)item["name"] == "Master"), Is.True);
            Assert.That(((JArray)read.Json["snapshots"]).Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(read.Json["exposedParameters"], Is.Not.Null);

            var go = new GameObject("MixerEmitter");
            var source = go.AddComponent<AudioSource>();
            var assign = new AssignToMixerGroupTool();
            var ambiguous = assign.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["mixerGroup"] = "Master",
            });
            Assert.That(ambiguous.IsError, Is.True);
            Assert.That(source.outputAudioMixerGroup, Is.Null);

            var routed = assign.Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["mixerGroup"] = "Master",
                ["mixerGuidOrPath"] = MixerAPath,
            });
            Assert.That(routed.IsError, Is.False, routed.Text);
            Assert.That(AssetDatabase.GetAssetPath(source.outputAudioMixerGroup.audioMixer), Is.EqualTo(MixerAPath));
            Assert.That((string)routed.Json["mixer"]?["assetPath"], Is.EqualTo(MixerAPath));

            Undo.PerformUndo();
            Assert.That(source.outputAudioMixerGroup, Is.Null);
        }

        [Test]
        public void RuntimeAudioTools_FailClosedInEditMode()
        {
            var go = new GameObject("RuntimeEmitter");
            go.AddComponent<AudioSource>().clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath);

            var play = new PlayAudioSourceTool().Execute(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["action"] = "play",
            });
            Assert.That(play.IsError, Is.True);
            Assert.That((string)play.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));

            var setParameter = new SetMixerParameterTool().Execute(new JObject
            {
                ["mixerGuidOrPath"] = MixerAPath,
                ["name"] = "MissingExposedParameter",
                ["value"] = 0f,
            });
            Assert.That(setParameter.IsError, Is.True);
            Assert.That((string)setParameter.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));

            var snapshotName = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerAPath).FindSnapshot("Snapshot") != null
                ? "Snapshot"
                : "Master";
            var snapshot = new PlayMixerSnapshotTool().Execute(new JObject
            {
                ["mixerGuidOrPath"] = MixerAPath,
                ["snapshot"] = snapshotName,
                ["transitionSeconds"] = 0f,
            });
            Assert.That(snapshot.IsError, Is.True);
            Assert.That((string)snapshot.Json["code"], Is.EqualTo("PLAY_MODE_REQUIRED"));
        }

        private static int Count<T>() where T : UnityEngine.Object
            => UnityObjectIds.FindAll<T>(FindObjectsInactive.Include).Length;

        private static AudioMixer CreateMixer(string path)
        {
            var controllerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.Audio.AudioMixerController", false))
                .FirstOrDefault(type => type != null);
            Assert.That(controllerType, Is.Not.Null, "UnityEditor.Audio.AudioMixerController");
            var create = controllerType.GetMethod(
                "CreateMixerControllerAtPath",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            Assert.That(create, Is.Not.Null, "CreateMixerControllerAtPath(string)");
            create.Invoke(null, new object[] { path });
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
        }

        private static void WritePcmWave(string path)
        {
            const int sampleRate = 8000;
            const short channels = 1;
            const short bitsPerSample = 16;
            const int sampleCount = 800;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                var dataBytes = sampleCount * channels * bitsPerSample / 8;
                writer.Write(new[] { 'R', 'I', 'F', 'F' }.Select(Convert.ToByte).ToArray());
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' }.Select(Convert.ToByte).ToArray());
                writer.Write(new[] { 'f', 'm', 't', ' ' }.Select(Convert.ToByte).ToArray());
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8);
                writer.Write((short)(channels * bitsPerSample / 8));
                writer.Write(bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' }.Select(Convert.ToByte).ToArray());
                writer.Write(dataBytes);
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = (short)(Math.Sin(2d * Math.PI * 440d * i / sampleRate) * short.MaxValue * 0.1d);
                    writer.Write(sample);
                }
            }
        }
    }
}
