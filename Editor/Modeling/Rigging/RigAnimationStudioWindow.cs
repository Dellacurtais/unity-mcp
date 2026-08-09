using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Modeling;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public sealed class RigAnimationStudioWindow : EditorWindow
    {
        private static readonly string[] Pages =
        {
            "Inspector", "Skin Weights", "Skeleton & Bind", "Clips & Poses", "Retarget", "IK & Bake", "Auto Rig",
        };

        [SerializeField] private GameObject target;
        [SerializeField] private int page;
        [SerializeField] private int rendererIndex;
        [SerializeField] private int boneIndex;
        [SerializeField] private AnimationClip clip;
        [SerializeField] private float clipTime;
        [SerializeField] private int sampleCount = 12;
        [SerializeField] private System.Collections.Generic.List<AnimationClip> validationClips = new System.Collections.Generic.List<AnimationClip>();
        [SerializeField] private GameObject splitPartsRoot;
        [SerializeField] private SmartBodySplitManifest splitManifest;
        [SerializeField] private float splitTolerance = 0.00001f;
        [SerializeField] private bool showSkeleton = true;
        [SerializeField] private bool showHeatmap = true;
        [SerializeField] private string boneFilter = string.Empty;
        [SerializeField] private bool paintWeights;
        [SerializeField] private SkinWeightBrushSettings brush = new SkinWeightBrushSettings();
        [SerializeField] private string lockedBoneIndices = string.Empty;
        [SerializeField] private string lockedVertexIndices = string.Empty;
        [SerializeField] private string regionVertexIndices = string.Empty;
        [SerializeField] private SkinnedMeshRenderer transferSource;
        [SerializeField] private Transform skeletonBone;
        [SerializeField] private Transform skeletonParent;
        [SerializeField] private string skeletonBoneName = "NewBone";
        [SerializeField] private Vector3 skeletonLocalPosition;
        [SerializeField] private Vector3 skeletonLocalEuler;
        [SerializeField] private Transform skeletonRootBone;
        [SerializeField] private RigBindPoseSnapshot bindPoseSnapshot;
        [SerializeField] private float transferMaximumDistance = 1f;
        [SerializeField] private bool transferMirrorRegion;
        [SerializeField] private string transferRegionIndices = string.Empty;
        [SerializeField] private RigPoseLibrary poseLibrary;
        [SerializeField] private string poseName = "Nova Pose";
        [SerializeField] private int poseIndex;
        [SerializeField] private string poseRename = string.Empty;
        [SerializeField] private Texture2D poseThumbnail;
        [SerializeField] private AnimationClipTransformSettings clipTransform = new AnimationClipTransformSettings();
        [SerializeField] private bool clipPlaying;
        [SerializeField] private bool clipLoop = true;
        [SerializeField] private GameObject retargetSource;
        [SerializeField] private GameObject retargetDestination;
        [SerializeField] private RigRetargetProfile retargetProfile;
        [SerializeField] private float retargetFrameRate = 30f;
        private RigRetargetMetrics retargetMetrics;
        [SerializeField] private RigIkBinding ikBinding;
        [SerializeField] private Transform ikRoot;
        [SerializeField] private Transform ikMid;
        [SerializeField] private Transform ikTip;
        [SerializeField] private RigIkSolverType ikSolver;
        [SerializeField] private TextAsset aiProposalJson;
        [SerializeField] private System.Collections.Generic.List<Texture2D> aiReferenceViews = new System.Collections.Generic.List<Texture2D>();
        private RigAutoRigProposal autoRigProposal;
        private Vector2 scroll;
        private RigTargetContext context;
        private RigDiagnosticReport diagnostic;
        private RigClipValidationReport clipReport;
        private RigClipPreviewSession preview;
        private double lastPlaybackTime;

        [MenuItem("Window/Asset Modeler/Rig & Animation Studio")]
        public static RigAnimationStudioWindow Open()
        {
            var window = GetWindow<RigAnimationStudioWindow>();
            window.titleContent = new GUIContent("Rig & Animation");
            window.minSize = new Vector2(440f, 520f);
            window.Show();
            return window;
        }

        public static void Open(GameObject value)
        {
            var window = Open();
            window.SetTarget(value);
        }

        private void OnEnable()
        {
            EditorApplication.update += UpdateClipPlayback;
            if (target == null && Selection.activeGameObject != null) target = Selection.activeGameObject;
            Refresh();
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdateClipPlayback;
            clipPlaying = false;
            StopPreview();
            RigAnimationSceneController.Clear(this);
            AutoRigPreviewController.Clear();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject == null) return;
            if (context != null && context.Root != null && Selection.activeGameObject.transform.IsChildOf(context.Root.transform))
            {
                Repaint();
                return;
            }
            SetTarget(Selection.activeGameObject);
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();
            page = position.width < 760f
                ? EditorGUILayout.Popup(new GUIContent("Ferramenta", "Cada fase possui uma página independente."), page, Pages)
                : GUILayout.Toolbar(page, Pages, GUILayout.Height(26f));
            EditorGUILayout.Space(4f);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (page)
            {
                case 0: DrawInspectorPage(); break;
                case 1: DrawSkinWeightsPage(); break;
                case 2: DrawSkeletonPage(); break;
                case 3: DrawClipsAndPosesPage(); break;
                case 4: DrawRetargetPage(); break;
                case 5: DrawIkPage(); break;
                case 6: DrawAutoRigPage(); break;
                default: DrawPhasePlaceholder(page + 1, Pages[page]); break;
            }
            EditorGUILayout.EndScrollView();
            SyncSceneState();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            var value = (GameObject)EditorGUILayout.ObjectField("Personagem", target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetTarget(value);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Usar seleção")) SetTarget(Selection.activeGameObject);
            if (GUILayout.Button("Atualizar")) Refresh();
            EditorGUILayout.EndHorizontal();
            if (context != null && context.Root != null)
                EditorGUILayout.LabelField($"{context.RigType} • {context.Renderers.Length} renderer(s) • {context.Bones.Length} bones", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawInspectorPage()
        {
            if (context == null || !context.IsValid)
            {
                EditorGUILayout.HelpBox("Selecione um personagem com SkinnedMeshRenderer.", MessageType.Info);
                return;
            }
            DrawRendererAndOverlay();
            DrawBoneTree();
            DrawClipValidation();
            DrawDiagnosticReport();
        }

        private void DrawSkinWeightsPage()
        {
            if (context == null || !context.IsValid)
            {
                EditorGUILayout.HelpBox("Selecione um personagem com SkinnedMeshRenderer.", MessageType.Info);
                return;
            }
            var names = context.Renderers.Select(value => RigTargetResolver.RelativePath(value.transform, context.Root.transform)).ToArray();
            rendererIndex = EditorGUILayout.Popup("Renderer", Mathf.Clamp(rendererIndex, 0, names.Length - 1), names);
            var renderer = context.Renderers[rendererIndex];
            var bones = renderer.bones ?? Array.Empty<Transform>();
            if (bones.Length == 0) { EditorGUILayout.HelpBox("Renderer sem bones.", MessageType.Error); return; }
            boneIndex = EditorGUILayout.Popup("Bone", Mathf.Clamp(boneIndex, 0, bones.Length - 1), bones.Select(value => value != null ? value.name : "<null>").ToArray());
            bool editable = RigAssetSafety.IsEditableStandaloneMesh(renderer.sharedMesh);
            if (!editable)
            {
                EditorGUILayout.HelpBox("A mesh atual é importada ou não é um .asset editável. O pincel permanece bloqueado.", MessageType.Warning);
                if (GUILayout.Button("Criar cópia editável de skin"))
                {
                    RigAssetSafety.CreateEditableSkinCopy(renderer);
                    Refresh();
                }
                return;
            }
            EditorGUILayout.HelpBox($"Editando cópia segura: {AssetDatabase.GetAssetPath(renderer.sharedMesh)}", MessageType.Info);
            brush.Mode = (SkinWeightPaintMode)EditorGUILayout.EnumPopup("Modo", brush.Mode);
            brush.Radius = EditorGUILayout.Slider("Raio", brush.Radius, 0.001f, Mathf.Max(0.01f, renderer.sharedMesh.bounds.size.magnitude));
            brush.Strength = EditorGUILayout.Slider("Força", brush.Strength, 0f, 1f);
            brush.Falloff = EditorGUILayout.Slider("Falloff", brush.Falloff, 0.1f, 4f);
            brush.MaximumInfluences = EditorGUILayout.IntSlider("Máx. influências", brush.MaximumInfluences, 1, 16);
            brush.PruneThreshold = EditorGUILayout.Slider("Prune", brush.PruneThreshold, 0f, 0.1f);
            brush.MirrorX = EditorGUILayout.ToggleLeft("Espelhar no X local", brush.MirrorX);
            brush.XRay = EditorGUILayout.ToggleLeft("X-Ray — pintar através da superfície", brush.XRay);
            brush.FrontFacesOnly = !brush.XRay && EditorGUILayout.ToggleLeft("Somente faces frontais", brush.FrontFacesOnly);
            EditorGUILayout.LabelField("Restrições", EditorStyles.boldLabel);
            lockedBoneIndices = EditorGUILayout.TextField(new GUIContent("Bones bloqueados", "Índices separados por vírgula ou intervalos, por exemplo 0,2,5-8."), lockedBoneIndices);
            lockedVertexIndices = EditorGUILayout.TextField(new GUIContent("Vértices bloqueados", "Índices separados por vírgula ou intervalos."), lockedVertexIndices);
            regionVertexIndices = EditorGUILayout.TextField(new GUIContent("Região permitida", "Vazio usa toda a mesh; informe índices ou intervalos para limitar a operação."), regionVertexIndices);
            paintWeights = GUILayout.Toggle(paintWeights, paintWeights ? "Pincel ativo — arraste no Scene View" : "Ativar pincel no Scene View", "Button", GUILayout.Height(30f));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Normalizar tudo"))
            {
                var settings = new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Normalize, Radius = float.MaxValue, MaximumInfluences = brush.MaximumInfluences, PruneThreshold = brush.PruneThreshold };
                SkinWeightEditorService.ApplyRadialBrush(renderer, boneIndex, Vector3.zero, settings,
                    ParseIndices(lockedBoneIndices), ParseIndices(lockedVertexIndices), null, ParseIndices(regionVertexIndices));
            }
            if (GUILayout.Button("Flood no bone"))
            {
                var settings = new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood, Strength = brush.Strength, MaximumInfluences = brush.MaximumInfluences, PruneThreshold = brush.PruneThreshold, MirrorX = brush.MirrorX };
                SkinWeightEditorService.ApplyRadialBrush(renderer, boneIndex, Vector3.zero, settings,
                    ParseIndices(lockedBoneIndices), ParseIndices(lockedVertexIndices), null, ParseIndices(regionVertexIndices));
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Mouse esquerdo pinta; Shift suaviza temporariamente; Ctrl subtrai; Escape desativa. Cada arraste é um único grupo de Undo.", MessageType.None);
        }

        private void DrawSkeletonPage()
        {
            if (context == null || !context.IsValid) { EditorGUILayout.HelpBox("Selecione um rig.", MessageType.Info); return; }
            var rendererNames = context.Renderers.Select(value => value.name).ToArray();
            rendererIndex = EditorGUILayout.Popup("Renderer", Mathf.Clamp(rendererIndex, 0, rendererNames.Length - 1), rendererNames);
            var renderer = context.Renderers[rendererIndex];
            EditorGUILayout.LabelField("Bind Pose", EditorStyles.boldLabel);
            if (!RigAssetSafety.IsEditableStandaloneMesh(renderer.sharedMesh))
            {
                EditorGUILayout.HelpBox("Rebuild e transferência exigem uma cópia .asset editável.", MessageType.Warning);
                if (GUILayout.Button("Criar cópia editável")) { RigAssetSafety.CreateEditableSkinCopy(renderer); Refresh(); }
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Recalcular bind poses")) BindPoseService.Rebuild(renderer);
                if (GUILayout.Button("Capturar snapshot")) bindPoseSnapshot = BindPoseService.Capture(renderer, context.Root);
                EditorGUILayout.EndHorizontal();
                bindPoseSnapshot = (RigBindPoseSnapshot)EditorGUILayout.ObjectField("Snapshot", bindPoseSnapshot, typeof(RigBindPoseSnapshot), false);
                using (new EditorGUI.DisabledScope(bindPoseSnapshot == null))
                    if (GUILayout.Button("Restaurar snapshot")) BindPoseService.Restore(renderer, context.Root, bindPoseSnapshot);
                skeletonRootBone = (Transform)EditorGUILayout.ObjectField("Root bone", skeletonRootBone != null ? skeletonRootBone : renderer.rootBone, typeof(Transform), true);
                if (GUILayout.Button("Definir root bone")) SkeletonEditorService.SetRootBone(renderer, skeletonRootBone);
                using (new EditorGUI.DisabledScope(validationClips.Count == 0))
                    if (GUILayout.Button("Remover bones sem uso (provar com batch)"))
                    {
                        var report = SkeletonEditorService.RemoveUnusedBones(renderer, context.Root, validationClips);
                        ShowNotification(new GUIContent($"{report.RemovedBoneCount} bones removidos")); Refresh();
                    }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editar skeleton", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            skeletonBone = (Transform)EditorGUILayout.ObjectField("Bone", skeletonBone, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck() && skeletonBone != null) { skeletonLocalPosition = skeletonBone.localPosition; skeletonLocalEuler = skeletonBone.localEulerAngles; }
            skeletonParent = (Transform)EditorGUILayout.ObjectField("Pai", skeletonParent, typeof(Transform), true);
            skeletonBoneName = EditorGUILayout.TextField("Nome", skeletonBoneName);
            skeletonLocalPosition = EditorGUILayout.Vector3Field("Posição local", skeletonLocalPosition);
            skeletonLocalEuler = EditorGUILayout.Vector3Field("Rotação local", skeletonLocalEuler);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Criar") && skeletonParent != null) skeletonBone = SkeletonEditorService.CreateBone(skeletonParent, skeletonBoneName, Vector3.zero);
            if (GUILayout.Button("Renomear") && skeletonBone != null) SkeletonEditorService.RenameBone(skeletonBone, skeletonBoneName);
            if (GUILayout.Button("Duplicar") && skeletonBone != null) skeletonBone = SkeletonEditorService.DuplicateBone(skeletonBone, false);
            if (GUILayout.Button("Reparentear") && skeletonBone != null && skeletonParent != null) SkeletonEditorService.ReparentBone(skeletonBone, skeletonParent);
            if (GUILayout.Button("Mover") && skeletonBone != null) SkeletonEditorService.MoveBone(skeletonBone, skeletonLocalPosition, Quaternion.Euler(skeletonLocalEuler));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Transferir pesos", EditorStyles.boldLabel);
            transferSource = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Renderer fonte", transferSource, typeof(SkinnedMeshRenderer), true);
            transferMaximumDistance = EditorGUILayout.FloatField("Distância máxima", Mathf.Max(0f, transferMaximumDistance));
            transferMirrorRegion = EditorGUILayout.ToggleLeft("Espelhar região no X", transferMirrorRegion);
            transferRegionIndices = EditorGUILayout.TextField(new GUIContent("Região destino", "Vazio transfere a mesh inteira; aceita índices e intervalos."), transferRegionIndices);
            using (new EditorGUI.DisabledScope(transferSource == null || !RigAssetSafety.IsEditableStandaloneMesh(renderer.sharedMesh)))
                if (GUILayout.Button("Transferir por topologia/superfície"))
                {
                    var report = SkinWeightTransferService.Transfer(transferSource, renderer, new WeightTransferOptions
                    {
                        MaximumDistance = transferMaximumDistance <= 0f ? float.PositiveInfinity : transferMaximumDistance,
                        MaximumInfluences = brush.MaximumInfluences,
                        PruneThreshold = brush.PruneThreshold,
                        IncludedVertices = ParseIndices(transferRegionIndices),
                        MirrorRegionX = transferMirrorRegion,
                    });
                    ShowNotification(new GUIContent($"{report.VertexCount} vértices • distância máx. {report.MaximumDistance:F5}"));
                }
        }

        private void DrawClipsAndPosesPage()
        {
            if (context == null || context.Root == null) { EditorGUILayout.HelpBox("Selecione um personagem.", MessageType.Info); return; }
            EditorGUILayout.LabelField("Pose Library", EditorStyles.boldLabel);
            poseLibrary = (RigPoseLibrary)EditorGUILayout.ObjectField("Library", poseLibrary, typeof(RigPoseLibrary), false);
            if (poseLibrary == null && GUILayout.Button("Criar Pose Library")) poseLibrary = RigPoseLibraryService.CreateLibrary(null);
            poseName = EditorGUILayout.TextField("Nome da pose", poseName);
            using (new EditorGUI.DisabledScope(poseLibrary == null))
            {
                if (GUILayout.Button("Capturar pose atual")) RigPoseLibraryService.Add(poseLibrary, RigPoseLibraryService.Capture(context.Root, poseName));
                if (poseLibrary != null && poseLibrary.Poses.Count > 0)
                {
                    poseIndex = EditorGUILayout.Popup("Pose", Mathf.Clamp(poseIndex, 0, poseLibrary.Poses.Count - 1), poseLibrary.Poses.Select(value => value.Name).ToArray());
                    var selectedPose = poseLibrary.Poses[Mathf.Clamp(poseIndex, 0, poseLibrary.Poses.Count - 1)];
                    poseRename = EditorGUILayout.TextField("Renomear", string.IsNullOrEmpty(poseRename) ? selectedPose.Name : poseRename);
                    poseThumbnail = (Texture2D)EditorGUILayout.ObjectField("Thumbnail", selectedPose.Thumbnail, typeof(Texture2D), false);
                    if (poseThumbnail != selectedPose.Thumbnail) { Undo.RecordObject(poseLibrary, "Set pose thumbnail"); selectedPose.Thumbnail = poseThumbnail; EditorUtility.SetDirty(poseLibrary); }
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Aplicar pose")) RigPoseLibraryService.Apply(context.Root, poseLibrary.Poses[poseIndex]);
                    if (GUILayout.Button("Renomear")) { RigPoseLibraryService.Rename(poseLibrary, selectedPose.Id, poseRename); poseRename = string.Empty; }
                    if (GUILayout.Button("Duplicar")) { RigPoseLibraryService.Duplicate(poseLibrary, selectedPose.Id); poseIndex = poseLibrary.Poses.Count - 1; poseRename = string.Empty; }
                    if (GUILayout.Button("Excluir")) { Undo.RecordObject(poseLibrary, "Delete rig pose"); poseLibrary.Remove(poseLibrary.Poses[poseIndex].Id); EditorUtility.SetDirty(poseLibrary); poseIndex = 0; }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Animation Clip Studio", EditorStyles.boldLabel);
            clip = (AnimationClip)EditorGUILayout.ObjectField("Clip fonte", clip, typeof(AnimationClip), false);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(clipPlaying ? "Pausar" : "Play")) { clipPlaying = !clipPlaying; lastPlaybackTime = EditorApplication.timeSinceStartup; }
            clipLoop = GUILayout.Toggle(clipLoop, "Loop", "Button");
            if (GUILayout.Button("Keyframe") && clip != null)
            {
                Transform keyTarget = skeletonBone;
                if (keyTarget == null && context.Renderers.Length > 0)
                {
                    var bones = context.Renderers[Mathf.Clamp(rendererIndex, 0, context.Renderers.Length - 1)].bones;
                    if (bones != null && bones.Length > 0) keyTarget = bones[Mathf.Clamp(boneIndex, 0, bones.Length - 1)];
                }
                if (keyTarget != null) AnimationClipStudioService.AddTransformKey(clip, context.Root.transform, keyTarget, clipTime * clip.length);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.BeginChangeCheck(); clipTime = EditorGUILayout.Slider("Timeline", clipTime, 0f, 1f);
            if (EditorGUI.EndChangeCheck() && clip != null) { if (preview == null) preview = new RigClipPreviewSession(context.Root); preview.Sample(clip, clipTime); }
            clipTransform.Operation = (AnimationClipTransformOperation)EditorGUILayout.EnumPopup("Operação", clipTransform.Operation);
            if (clipTransform.Operation == AnimationClipTransformOperation.Trim)
            {
                clipTransform.StartTime = EditorGUILayout.FloatField("Início (s)", clipTransform.StartTime);
                clipTransform.EndTime = EditorGUILayout.FloatField("Fim (s)", clipTransform.EndTime);
            }
            if (clipTransform.Operation == AnimationClipTransformOperation.ScaleTime)
                clipTransform.TimeScale = EditorGUILayout.FloatField("Escala de tempo", clipTransform.TimeScale);
            if (clipTransform.Operation == AnimationClipTransformOperation.AdditiveReference)
                clipTransform.ReferenceClip = (AnimationClip)EditorGUILayout.ObjectField("Clip referência", clipTransform.ReferenceClip, typeof(AnimationClip), false);
            if (clipTransform.Operation == AnimationClipTransformOperation.CopyRemapCurves)
            {
                clipTransform.SourcePathPrefix = EditorGUILayout.TextField("Prefixo fonte", clipTransform.SourcePathPrefix);
                clipTransform.DestinationPathPrefix = EditorGUILayout.TextField("Prefixo destino", clipTransform.DestinationPathPrefix);
            }
            if (clipTransform.Operation == AnimationClipTransformOperation.MergeSequential)
            {
                if (clipTransform.AdditionalClips == null) clipTransform.AdditionalClips = new System.Collections.Generic.List<AnimationClip>();
                for (int i = 0; i < clipTransform.AdditionalClips.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal(); clipTransform.AdditionalClips[i] = (AnimationClip)EditorGUILayout.ObjectField(clipTransform.AdditionalClips[i], typeof(AnimationClip), false);
                    if (GUILayout.Button("×", GUILayout.Width(24f))) clipTransform.AdditionalClips.RemoveAt(i--); EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("Adicionar clip ao merge")) clipTransform.AdditionalClips.Add(null);
            }
            clipTransform.OutputFolder = EditorGUILayout.TextField("Pasta", clipTransform.OutputFolder);
            using (new EditorGUI.DisabledScope(clip == null))
                if (GUILayout.Button("Gerar clip standalone"))
                {
                    clip = AnimationClipStudioService.Transform(clip, clipTransform);
                    Selection.activeObject = clip;
                }
        }

        private void UpdateClipPlayback()
        {
            if (!clipPlaying || clip == null || context == null || context.Root == null || clip.length <= 0f) return;
            double now = EditorApplication.timeSinceStartup; double delta = Math.Max(0d, now - lastPlaybackTime); lastPlaybackTime = now;
            clipTime += (float)(delta / clip.length);
            if (clipTime >= 1f)
            {
                if (clipLoop) clipTime %= 1f;
                else { clipTime = 1f; clipPlaying = false; }
            }
            if (preview == null) preview = new RigClipPreviewSession(context.Root); preview.Sample(clip, clipTime); Repaint();
        }

        private void DrawRetargetPage()
        {
            EditorGUILayout.LabelField("Retarget Humanoid / Generic", EditorStyles.boldLabel);
            retargetSource = (GameObject)EditorGUILayout.ObjectField("Fonte", retargetSource, typeof(GameObject), true);
            retargetDestination = (GameObject)EditorGUILayout.ObjectField("Destino", retargetDestination != null ? retargetDestination : target, typeof(GameObject), true);
            retargetProfile = (RigRetargetProfile)EditorGUILayout.ObjectField("Profile", retargetProfile, typeof(RigRetargetProfile), false);
            using (new EditorGUI.DisabledScope(retargetSource == null || retargetDestination == null))
                if (GUILayout.Button("Analisar e criar profile")) retargetProfile = RigRetargetAnalyzer.Analyze(retargetSource, retargetDestination).Profile;
            if (retargetProfile != null)
            {
                int mapped = retargetProfile.Bones.Count(value => value.Enabled && !string.IsNullOrEmpty(value.DestinationPath));
                EditorGUILayout.HelpBox($"{mapped}/{retargetProfile.Bones.Count} bones mapeados. Confirme os itens de baixa confiança no Inspector do profile.", mapped == retargetProfile.Bones.Count ? MessageType.Info : MessageType.Warning);
                var serializedProfile = new SerializedObject(retargetProfile); serializedProfile.Update();
                EditorGUILayout.PropertyField(serializedProfile.FindProperty("RootScale"));
                EditorGUILayout.PropertyField(serializedProfile.FindProperty("TransferRootMotion"));
                EditorGUILayout.PropertyField(serializedProfile.FindProperty("Bones"), new GUIContent("Mapeamento por bone"), true);
                serializedProfile.ApplyModifiedProperties();
                if (GUILayout.Button("Enquadrar fonte e destino lado a lado") && retargetSource != null && retargetDestination != null)
                {
                    Selection.objects = new UnityEngine.Object[] { retargetSource, retargetDestination }; SceneView.lastActiveSceneView?.FrameSelected();
                }
                using (new EditorGUI.DisabledScope(retargetSource == null || retargetDestination == null))
                    if (GUILayout.Button("Aplicar pose para preview")) { RigRetargetService.ApplyPose(retargetSource, retargetDestination, retargetProfile); retargetMetrics = RigRetargetService.Measure(retargetSource, retargetDestination, retargetProfile); }
                if (retargetMetrics != null)
                    EditorGUILayout.HelpBox($"Erro posicional — Head {retargetMetrics.HeadPositionError:F4}, Hands L/R {retargetMetrics.LeftHandPositionError:F4}/{retargetMetrics.RightHandPositionError:F4}, Feet L/R {retargetMetrics.LeftFootPositionError:F4}/{retargetMetrics.RightFootPositionError:F4}\nRotação máxima {retargetMetrics.MaximumRotationError:F2}°", MessageType.Info);
                clip = (AnimationClip)EditorGUILayout.ObjectField("Clip fonte", clip, typeof(AnimationClip), false);
                retargetFrameRate = EditorGUILayout.Slider("Frame rate", retargetFrameRate, 1f, 120f);
                using (new EditorGUI.DisabledScope(clip == null || retargetSource == null || retargetDestination == null))
                    if (GUILayout.Button("Bake retarget para .anim")) { clip = RigRetargetService.RetargetClip(retargetSource, retargetDestination, retargetProfile, clip, retargetFrameRate); Selection.activeObject = clip; }
                using (new EditorGUI.DisabledScope(validationClips.All(value => value == null) || retargetSource == null || retargetDestination == null))
                    if (GUILayout.Button("Bake batch de clips"))
                    {
                        var outputs = RigRetargetService.RetargetClips(retargetSource, retargetDestination, retargetProfile, validationClips, retargetFrameRate);
                        Selection.objects = outputs.Cast<UnityEngine.Object>().ToArray();
                    }
            }
        }

        private void DrawIkPage()
        {
            if (context == null || context.Root == null) { EditorGUILayout.HelpBox("Selecione um personagem.", MessageType.Info); return; }
            EditorGUILayout.LabelField("Animation Rigging / IK", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AnimationRiggingAdapter.Status, MessageType.Info);
            ikBinding = (RigIkBinding)EditorGUILayout.ObjectField("Binding", ikBinding, typeof(RigIkBinding), true);
            ikSolver = (RigIkSolverType)EditorGUILayout.EnumPopup("Solver", ikSolver);
            ikRoot = (Transform)EditorGUILayout.ObjectField("Root", ikRoot, typeof(Transform), true);
            ikMid = (Transform)EditorGUILayout.ObjectField("Mid", ikMid, typeof(Transform), true);
            ikTip = (Transform)EditorGUILayout.ObjectField("Tip", ikTip, typeof(Transform), true);
            using (new EditorGUI.DisabledScope(ikRoot == null || ikTip == null || (ikSolver == RigIkSolverType.TwoBone && ikMid == null)))
                if (GUILayout.Button("Criar binding + target + hint")) ikBinding = RigIkService.Create(context.Root, ikRoot, ikMid, ikTip, ikSolver);
            if (ikBinding != null)
            {
                EditorGUILayout.HelpBox("Selecione o binding no Hierarchy para manipular Target e Hint diretamente no Scene View.", MessageType.None);
                var serializedBinding = new SerializedObject(ikBinding); serializedBinding.Update();
                EditorGUILayout.PropertyField(serializedBinding.FindProperty("Chain"), new GUIContent("Chain FABRIK"), true);
                EditorGUILayout.PropertyField(serializedBinding.FindProperty("Iterations"));
                EditorGUILayout.PropertyField(serializedBinding.FindProperty("Weight"));
                EditorGUILayout.PropertyField(serializedBinding.FindProperty("PlacementPreset"));
                if (serializedBinding.ApplyModifiedProperties()) NativeRigIkSolver.CaptureRestPose(ikBinding);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Preset mão à frente")) RigIkService.ApplyPlacementPreset(ikBinding, RigIkPlacementPreset.HandReachForward);
                if (GUILayout.Button("Preset mão no peito")) RigIkService.ApplyPlacementPreset(ikBinding, RigIkPlacementPreset.HandAtChest);
                if (GUILayout.Button("Preset pé no chão")) RigIkService.ApplyPlacementPreset(ikBinding, RigIkPlacementPreset.FootPlantGround);
                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("Capturar pose base da chain")) NativeRigIkSolver.CaptureRestPose(ikBinding);
                if (GUILayout.Button("Resolver preview")) NativeRigIkSolver.Solve(ikBinding);
                if (AnimationRiggingAdapter.IsAvailable && GUILayout.Button("Criar constraint equivalente do Animation Rigging"))
                {
                    if (!AnimationRiggingAdapter.TryCreateConstraint(ikBinding, out var constraint, out var message)) EditorUtility.DisplayDialog("Animation Rigging", message, "OK");
                    else { ShowNotification(new GUIContent(message)); Selection.activeObject = constraint; }
                }
                clip = (AnimationClip)EditorGUILayout.ObjectField("Clip fonte", clip, typeof(AnimationClip), false);
                retargetFrameRate = EditorGUILayout.Slider("Frame rate", retargetFrameRate, 1f, 120f);
                using (new EditorGUI.DisabledScope(clip == null))
                    if (GUILayout.Button("Bake IK para .anim")) { clip = RigIkService.Bake(context.Root, ikBinding, clip, retargetFrameRate); Selection.activeObject = clip; }
            }
        }

        private void DrawAutoRigPage()
        {
            if (target == null) { EditorGUILayout.HelpBox("Selecione uma mesh estática ou skinned.", MessageType.Info); return; }
            EditorGUILayout.LabelField("Auto Rig + assistência por IA", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("A análise local não envia assets. A aplicação cria uma mesh .asset, skeleton e pesos iniciais; nunca altera o FBX.", MessageType.Info);
            if (GUILayout.Button("Analisar mesh localmente")) { autoRigProposal = HumanoidAutoRigAnalyzer.Analyze(target); AutoRigPreviewController.Show(autoRigProposal); }
            EditorGUILayout.LabelField("Referências multi-view (opcionais)", EditorStyles.miniBoldLabel);
            for (int i = 0; i < aiReferenceViews.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                aiReferenceViews[i] = (Texture2D)EditorGUILayout.ObjectField(aiReferenceViews[i], typeof(Texture2D), false);
                if (GUILayout.Button("×", GUILayout.Width(24f))) aiReferenceViews.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Adicionar vista")) aiReferenceViews.Add(null);
            if (GUILayout.Button("Exportar pedido para IA…"))
            {
                string path = EditorUtility.SaveFilePanel("Exportar pedido Auto Rig", string.Empty, target.name + "_auto-rig-request.json", "json");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    RigAiProposalService.ExportRequest(target, aiReferenceViews.Where(value => value != null).Select(AssetDatabase.GetAssetPath), path);
                    EditorUtility.RevealInFinder(path);
                }
            }
            aiProposalJson = (TextAsset)EditorGUILayout.ObjectField("Proposta IA JSON", aiProposalJson, typeof(TextAsset), false);
            using (new EditorGUI.DisabledScope(aiProposalJson == null))
                if (GUILayout.Button("Importar proposta IA validada")) { autoRigProposal = RigAiProposalService.ImportJson(target, aiProposalJson.text); AutoRigPreviewController.Show(autoRigProposal); }
            if (autoRigProposal == null) return;
            EditorGUILayout.HelpBox($"Confiança média {autoRigProposal.AverageConfidence:P0}\nMesh {autoRigProposal.MeshHash.Substring(0, 12)}…\nProposta {autoRigProposal.ProposalHash.Substring(0, 12)}…", autoRigProposal.CanApply ? MessageType.Info : MessageType.Warning);
            EditorGUILayout.LabelField("Landmarks — correção manual", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            foreach (var landmark in autoRigProposal.Landmarks)
            {
                EditorGUILayout.BeginHorizontal(); EditorGUILayout.LabelField(landmark.Id, GUILayout.Width(120f)); landmark.LocalPosition = EditorGUILayout.Vector3Field(GUIContent.none, landmark.LocalPosition); landmark.Confidence = EditorGUILayout.Slider(landmark.Confidence, 0f, 1f, GUILayout.Width(140f)); EditorGUILayout.EndHorizontal();
            }
            if (EditorGUI.EndChangeCheck()) { autoRigProposal.Rehash(); RigAutoRigProposalRegistry.Store(autoRigProposal); AutoRigPreviewController.Show(autoRigProposal); }
            if (!autoRigProposal.CanApply) EditorGUILayout.HelpBox("Corrija os landmarks bloqueadores: " + string.Join(", ", autoRigProposal.BlockingLandmarks), MessageType.Error);
            using (new EditorGUI.DisabledScope(!autoRigProposal.CanApply))
                if (GUILayout.Button("Aplicar Auto Rig…", GUILayout.Height(30f)) && EditorUtility.DisplayDialog("Aplicar Auto Rig", "Criar cópia editável, skeleton e pesos iniciais? O FBX permanecerá intacto.", "Aplicar", "Cancelar"))
                {
                    var result = HumanoidAutoRigService.Apply(autoRigProposal, autoRigProposal.ProposalHash, autoRigProposal.MeshHash);
                    ShowNotification(new GUIContent(result.AvatarStatus));
                    Selection.activeObject = result.Renderer.gameObject; SetTarget(result.Renderer.gameObject); AutoRigPreviewController.Clear();
                }
        }

        private void DrawRendererAndOverlay()
        {
            EditorGUILayout.LabelField("Visualização", EditorStyles.boldLabel);
            var names = context.Renderers.Select(renderer => RigTargetResolver.RelativePath(renderer.transform, context.Root.transform)).ToArray();
            rendererIndex = EditorGUILayout.Popup("Renderer", Mathf.Clamp(rendererIndex, 0, names.Length - 1), names);
            showSkeleton = EditorGUILayout.ToggleLeft("Skeleton no Scene View", showSkeleton);
            showHeatmap = EditorGUILayout.ToggleLeft("Heatmap do bone selecionado", showHeatmap);
            EditorGUILayout.Space(4f);
        }

        private void DrawBoneTree()
        {
            EditorGUILayout.LabelField("Skeleton", EditorStyles.boldLabel);
            boneFilter = EditorGUILayout.TextField("Buscar", boneFilter);
            var renderer = context.Renderers[Mathf.Clamp(rendererIndex, 0, context.Renderers.Length - 1)];
            var bones = renderer.bones ?? Array.Empty<Transform>();
            if (bones.Length == 0) { EditorGUILayout.HelpBox("Renderer sem bones.", MessageType.Warning); return; }
            boneIndex = Mathf.Clamp(boneIndex, 0, bones.Length - 1);
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                string path = RigTargetResolver.RelativePath(bone, context.Root.transform);
                if (!string.IsNullOrWhiteSpace(boneFilter)
                    && path.IndexOf(boneFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int depth = string.IsNullOrEmpty(path) ? 0 : path.Count(character => character == '/');
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(Mathf.Min(depth * 10f, 80f));
                bool selected = i == boneIndex;
                if (GUILayout.Toggle(selected, bone != null ? bone.name : $"<null {i}>", "Button"))
                {
                    boneIndex = i;
                    if (Event.current != null && Event.current.clickCount == 2 && bone != null)
                    {
                        Selection.activeTransform = bone;
                        SceneView.lastActiveSceneView?.FrameSelected();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space(4f);
        }

        private void DrawClipValidation()
        {
            EditorGUILayout.LabelField("Validação por clip", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            clip = (AnimationClip)EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false);
            if (EditorGUI.EndChangeCheck()) { StopPreview(); clipTime = 0f; }
            using (new EditorGUI.DisabledScope(clip == null))
            {
                EditorGUI.BeginChangeCheck();
                clipTime = EditorGUILayout.Slider("Tempo", clipTime, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    if (preview == null) preview = new RigClipPreviewSession(context.Root);
                    preview.Sample(clip, clipTime);
                }
                sampleCount = EditorGUILayout.IntSlider("Amostras", sampleCount, 2, 64);
                if (GUILayout.Button("Adicionar clip ao batch") && !validationClips.Contains(clip)) validationClips.Add(clip);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Validar clip atual"))
                    clipReport = RigClipValidationService.Validate(context.Root, new[] { clip }, sampleCount, splitPartsRoot, splitManifest, splitTolerance);
                if (GUILayout.Button("Restaurar pose")) StopPreview();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.LabelField("Batch", EditorStyles.miniBoldLabel);
            for (int i = 0; i < validationClips.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                validationClips[i] = (AnimationClip)EditorGUILayout.ObjectField(validationClips[i], typeof(AnimationClip), false);
                if (GUILayout.Button("×", GUILayout.Width(24f))) { validationClips.RemoveAt(i--); }
                EditorGUILayout.EndHorizontal();
            }
            splitPartsRoot = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Split parts root", "Opcional: valida gaps/RMS do Smart Body Split."), splitPartsRoot, typeof(GameObject), true);
            splitManifest = (SmartBodySplitManifest)EditorGUILayout.ObjectField("Split manifest", splitManifest, typeof(SmartBodySplitManifest), false);
            splitTolerance = EditorGUILayout.FloatField("Tolerância split", Mathf.Max(0f, splitTolerance));
            using (new EditorGUI.DisabledScope(validationClips.All(value => value == null)))
                if (GUILayout.Button("Validar batch")) clipReport = RigClipValidationService.Validate(context.Root, validationClips.Where(value => value != null).ToArray(), sampleCount, splitPartsRoot, splitManifest, splitTolerance);
            if (clipReport != null)
            {
                EditorGUILayout.HelpBox(clipReport.IsValid ? "Clip aprovado em todas as amostras." : "O clip possui falhas; veja o relatório.",
                    clipReport.IsValid ? MessageType.Info : MessageType.Error);
                if (GUILayout.Button("Exportar relatório JSON…"))
                {
                    string path = EditorUtility.SaveFilePanel("Exportar validação de clips", string.Empty, context.Root.name + "_rig-validation.json", "json");
                    if (!string.IsNullOrWhiteSpace(path)) RigClipValidationService.Export(clipReport, path);
                }
            }
        }

        private void DrawDiagnosticReport()
        {
            EditorGUILayout.LabelField("Diagnóstico", EditorStyles.boldLabel);
            if (diagnostic == null) return;
            EditorGUILayout.LabelField($"Vértices: {diagnostic.VertexCount:N0}   Influências: média {diagnostic.AverageInfluences:F2}, máx. {diagnostic.MaximumInfluences}");
            EditorGUILayout.LabelField($"Sem peso: {diagnostic.UnweightedVertexCount:N0}   Pesos inválidos: {diagnostic.InvalidWeightCount:N0}");
            if (diagnostic.Issues.Count == 0) EditorGUILayout.HelpBox("Rig aprovado sem avisos.", MessageType.Info);
            for (int i = 0; i < diagnostic.Issues.Count; i++)
            {
                var issue = diagnostic.Issues[i];
                var type = issue.Severity == RigDiagnosticSeverity.Error ? MessageType.Error
                    : issue.Severity == RigDiagnosticSeverity.Warning ? MessageType.Warning : MessageType.Info;
                EditorGUILayout.HelpBox($"{issue.Code}: {issue.Message}\n{issue.TargetPath}", type);
            }
        }

        private void DrawPhasePlaceholder(int phase, string label)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox($"Fase {phase} será habilitada após a aprovação automatizada da fase anterior.", MessageType.Info);
        }

        private void SetTarget(GameObject value)
        {
            StopPreview();
            target = value;
            Refresh();
        }

        private void Refresh()
        {
            context = RigTargetResolver.Resolve(target);
            if (context.Root != null) target = context.Root;
            diagnostic = RigInspectorService.Inspect(context);
            rendererIndex = context.Renderers.Length == 0 ? 0 : Mathf.Clamp(rendererIndex, 0, context.Renderers.Length - 1);
            boneIndex = 0;
            clipReport = null;
            SyncSceneState();
            Repaint();
        }

        private void StopPreview()
        {
            preview?.Dispose();
            preview = null;
        }

        private void SyncSceneState()
        {
            SkinnedMeshRenderer renderer = null;
            if (context != null && context.Renderers.Length > 0)
                renderer = context.Renderers[Mathf.Clamp(rendererIndex, 0, context.Renderers.Length - 1)];
            RigAnimationSceneController.Set(this, (page == 0 || page == 1) ? context : null, renderer, boneIndex,
                page == 0 && showSkeleton, (page == 0 && showHeatmap) || page == 1, page == 1 && paintWeights, brush,
                ParseIndices(lockedBoneIndices), ParseIndices(lockedVertexIndices), ParseIndices(regionVertexIndices));
        }

        private static HashSet<int> ParseIndices(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var result = new HashSet<int>();
            var tokens = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
            {
                var range = tokens[tokenIndex].Split('-');
                if (!int.TryParse(range[0], out int start) || start < 0) continue;
                int end = start;
                if (range.Length == 2 && (!int.TryParse(range[1], out end) || end < start)) continue;
                for (int value = start; value <= end && value - start <= 1000000; value++) result.Add(value);
            }
            return result.Count > 0 ? result : null;
        }
    }

    [InitializeOnLoad]
    internal static class RigAnimationSceneController
    {
        private static EditorWindow owner;
        private static RigTargetContext context;
        private static SkinnedMeshRenderer renderer;
        private static int boneIndex;
        private static bool skeleton;
        private static bool heatmap;
        private static bool paint;
        private static SkinWeightBrushSettings brush;
        private static ISet<int> lockedBones;
        private static ISet<int> lockedVertices;
        private static ISet<int> includedVertices;
        private static int undoGroup = -1;
        private static Mesh heatmapBake;
        private static int heatmapMeshId;
        private static int heatmapBone = -1;
        private static string heatmapWeightHash;
        private static float[] heatmapValues = Array.Empty<float>();
        private static Mesh paintBake;
        private static SkinWeightSpatialIndex paintSpatialIndex;
        private static Vector3[] paintVertices = Array.Empty<Vector3>();
        private static int paintRendererId;
        private static int paintMeshId;
        private static int paintPoseSignature;
        private static string sceneStateSignature;

        static RigAnimationSceneController() => SceneView.duringSceneGui += Draw;

        public static void Set(EditorWindow value, RigTargetContext target, SkinnedMeshRenderer skin, int selectedBone, bool showSkeleton, bool showHeatmap, bool paintWeights, SkinWeightBrushSettings settings,
            ISet<int> boneLocks, ISet<int> vertexLocks, ISet<int> region)
        {
            if (undoGroup >= 0 && (owner != value || renderer != skin || (paint && !paintWeights))) FinishStroke(false);
            string signature = string.Join("|", value != null ? value.GetInstanceID() : 0, target?.StateHash ?? string.Empty, skin != null ? skin.GetInstanceID() : 0, selectedBone, showSkeleton, showHeatmap, paintWeights,
                settings?.Mode.ToString() ?? string.Empty, settings?.Radius ?? 0f, settings?.Strength ?? 0f, settings?.MirrorX ?? false, settings?.XRay ?? false,
                SetSignature(boneLocks), SetSignature(vertexLocks), SetSignature(region));
            bool rendererChanged = renderer != skin;
            owner = value; context = target; renderer = skin; boneIndex = selectedBone; skeleton = showSkeleton; heatmap = showHeatmap; paint = paintWeights; brush = settings;
            lockedBones = boneLocks; lockedVertices = vertexLocks; includedVertices = region;
            if (rendererChanged) InvalidatePaintSpatialCache(false);
            if (!string.Equals(sceneStateSignature, signature, StringComparison.Ordinal)) { sceneStateSignature = signature; SceneView.RepaintAll(); }
        }

        public static void Clear(EditorWindow value)
        {
            if (owner != value) return;
            FinishStroke(false);
            if (heatmapBake != null) UnityEngine.Object.DestroyImmediate(heatmapBake); heatmapBake = null; heatmapValues = Array.Empty<float>();
            InvalidatePaintSpatialCache(true);
            owner = null; context = null; renderer = null;
            sceneStateSignature = string.Empty;
            SceneView.RepaintAll();
        }

        private static string SetSignature(IEnumerable<int> values) => values == null ? string.Empty : string.Join(",", values.OrderBy(value => value));

        private static void Draw(SceneView sceneView)
        {
            if (owner == null || context == null) return;
            if (skeleton) DrawSkeleton();
            if (heatmap && renderer != null) DrawHeatmap(sceneView);
            if (paint && renderer != null) HandlePaint(sceneView);
        }

        private static void DrawSkeleton()
        {
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            var bones = context.Bones;
            var set = bones.Where(bone => bone != null).ToHashSet();
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;
                Handles.color = new Color(0.2f, 0.85f, 1f, 0.9f);
                if (bone.parent != null && set.Contains(bone.parent)) Handles.DrawAAPolyLine(3f, bone.position, bone.parent.position);
                float size = HandleUtility.GetHandleSize(bone.position) * 0.025f;
                Handles.SphereHandleCap(0, bone.position, Quaternion.identity, size, EventType.Repaint);
            }
        }

        private static void DrawHeatmap(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones ?? Array.Empty<Transform>();
            if (mesh == null || boneIndex < 0 || boneIndex >= bones.Length) return;
            if (heatmapMeshId != mesh.GetInstanceID() || heatmapBone != boneIndex || heatmapWeightHash != context.SkinWeightHash)
            {
                heatmapMeshId = mesh.GetInstanceID(); heatmapBone = boneIndex; heatmapWeightHash = context.SkinWeightHash;
                heatmapValues = RigInspectorService.ComputeInfluenceHeatmap(mesh, boneIndex);
            }
            if (heatmapBake == null) heatmapBake = new Mesh { name = "RigHeatmapPreview" };
            renderer.BakeMesh(heatmapBake);
                var vertices = heatmapBake.vertices;
                int stride = Mathf.Max(1, vertices.Length / 2500);
                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                for (int i = 0; i < vertices.Length && i < heatmapValues.Length; i += stride)
                {
                    if (heatmapValues[i] <= 0.001f) continue;
                    var world = renderer.transform.TransformPoint(vertices[i]);
                    Handles.color = Color.Lerp(new Color(0.05f, 0.2f, 1f, 0.65f), new Color(1f, 0.05f, 0f, 0.95f), heatmapValues[i]);
                    Handles.DotHandleCap(0, world, Quaternion.identity, HandleUtility.GetHandleSize(world) * 0.012f, EventType.Repaint);
                }
                Handles.BeginGUI();
                GUILayout.BeginArea(new Rect(12f, sceneView.position.height - 62f, 250f, 42f), GUI.skin.box);
                GUILayout.Label($"Heatmap: {bones[boneIndex].name}  0 → 1", EditorStyles.whiteMiniLabel);
                GUILayout.EndArea();
                Handles.EndGUI();
        }

        private static void HandlePaint(SceneView sceneView)
        {
            var current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                FinishStroke(false); paint = false; current.Use(); owner.Repaint(); SceneView.RepaintAll(); return;
            }
            if (current.type == EventType.MouseUp && current.button == 0 && undoGroup >= 0)
            {
                FinishStroke(true); current.Use(); return;
            }
            if (current.alt) return;
            if (current.type == EventType.Layout) { HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive)); return; }
            var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (!TryRaycastMesh(renderer, ray, brush != null && brush.FrontFacesOnly, current.type == EventType.MouseDown, out var localPoint, out var worldPoint, out var worldNormal, out var posedVertices)) return;
            Handles.color = new Color(1f, 0.55f, 0.05f, 0.95f);
            Handles.DrawWireDisc(worldPoint, worldNormal, brush.Radius * renderer.transform.lossyScale.magnitude / Mathf.Sqrt(3f), 2f);
            if ((current.type == EventType.MouseDown || current.type == EventType.MouseDrag) && current.button == 0)
            {
                if (current.type == EventType.MouseDown)
                {
                    Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Paint skin weights stroke");
                    Undo.RegisterCompleteObjectUndo(renderer.sharedMesh, "Paint skin weights stroke");
                }
                var active = CloneSettings(brush);
                if (current.shift) active.Mode = SkinWeightPaintMode.Smooth;
                else if (current.control) active.Mode = SkinWeightPaintMode.Subtract;
                try
                {
                    IReadOnlyList<int> candidates = active.Mode == SkinWeightPaintMode.Flood || active.Mode == SkinWeightPaintMode.Normalize
                        ? null : paintSpatialIndex?.QuerySphere(localPoint, Mathf.Max(active.Radius, 1e-6f));
                    SkinWeightEditorService.ApplyRadialBrush(renderer, boneIndex, localPoint, active, lockedBones, lockedVertices, posedVertices, includedVertices, false, candidates);
                    heatmapWeightHash = string.Empty;
                }
                catch
                {
                    FinishStroke(false);
                    throw;
                }
                current.Use(); SceneView.RepaintAll(); owner.Repaint();
            }
        }

        private static void FinishStroke(bool commit)
        {
            InvalidatePaintSpatialCache(false);
            if (undoGroup < 0) return;
            int group = undoGroup; undoGroup = -1;
            if (commit) Undo.CollapseUndoOperations(group);
            else Undo.RevertAllDownToGroup(group);
        }

        private static SkinWeightBrushSettings CloneSettings(SkinWeightBrushSettings value) => new SkinWeightBrushSettings
        {
            Mode = value.Mode, Radius = value.Radius, Strength = value.Strength, Falloff = value.Falloff,
            MaximumInfluences = value.MaximumInfluences, PruneThreshold = value.PruneThreshold,
            MirrorX = value.MirrorX, FrontFacesOnly = value.FrontFacesOnly, XRay = value.XRay,
        };

        private static bool TryRaycastMesh(SkinnedMeshRenderer skin, Ray worldRay, bool frontOnly, bool forceRefresh, out Vector3 localPoint, out Vector3 worldPoint, out Vector3 worldNormal, out Vector3[] posedVertices)
        {
            localPoint = worldPoint = worldNormal = Vector3.zero; posedVertices = Array.Empty<Vector3>();
            var mesh = skin.sharedMesh;
            if (mesh == null) return false;
            EnsurePaintSpatialCache(skin, forceRefresh);
            if (paintSpatialIndex == null) return false;
            var inverse = skin.transform.worldToLocalMatrix;
            var ray = new Ray(inverse.MultiplyPoint(worldRay.origin), inverse.MultiplyVector(worldRay.direction).normalized);
            if (!paintSpatialIndex.Raycast(ray, frontOnly, out float distance, out Vector3 localNormal)) return false;
            posedVertices = paintVertices;
            localPoint = ray.GetPoint(distance);
            worldPoint = skin.transform.TransformPoint(localPoint);
            worldNormal = skin.transform.TransformDirection(localNormal).normalized;
            return true;
        }

        private static void EnsurePaintSpatialCache(SkinnedMeshRenderer skin, bool force)
        {
            var mesh = skin != null ? skin.sharedMesh : null; if (mesh == null) { InvalidatePaintSpatialCache(false); return; }
            int pose = ComputePoseSignature(skin); int rendererId = skin.GetInstanceID(); int meshId = mesh.GetInstanceID();
            if (!force && paintSpatialIndex != null && paintRendererId == rendererId && paintMeshId == meshId && paintPoseSignature == pose) return;
            if (paintBake == null) paintBake = new Mesh { name = "RigWeightPaintSpatialCache" };
            skin.BakeMesh(paintBake); paintVertices = paintBake.vertices;
            paintSpatialIndex = new SkinWeightSpatialIndex(paintVertices, mesh.triangles);
            paintRendererId = rendererId; paintMeshId = meshId; paintPoseSignature = pose;
        }

        private static int ComputePoseSignature(SkinnedMeshRenderer skin)
        {
            unchecked
            {
                int hash = 17; hash = hash * 31 + skin.transform.localToWorldMatrix.GetHashCode();
                var bones = skin.bones ?? Array.Empty<Transform>();
                for (int i = 0; i < bones.Length; i++) hash = hash * 31 + (bones[i] != null ? bones[i].localToWorldMatrix.GetHashCode() : 0);
                return hash;
            }
        }

        private static void InvalidatePaintSpatialCache(bool destroyMesh)
        {
            paintSpatialIndex = null; paintVertices = Array.Empty<Vector3>(); paintRendererId = paintMeshId = paintPoseSignature = 0;
            if (destroyMesh && paintBake != null) { UnityEngine.Object.DestroyImmediate(paintBake); paintBake = null; }
        }
    }
}
