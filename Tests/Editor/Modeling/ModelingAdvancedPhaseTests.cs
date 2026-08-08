using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Deform;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform;
using Maicon.SceneAnalyzer.Editor.Modeling.SceneTools;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using Maicon.SceneAnalyzer.Editor.Modeling.Spatial;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class ModelingAdvancedPhaseTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private readonly List<string> _assetFolders = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null && !AssetDatabase.Contains(_objects[i])) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            for (int i = _assetFolders.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(_assetFolders[i])) AssetDatabase.DeleteAsset(_assetFolders[i]);
            _assetFolders.Clear();
            Undo.ClearAll();
        }

        [Test]
        public void ChannelTransfer_InterpolatesRigAndBlendShapesForNewVertex()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var snapshot = MeshChannelSnapshot.Capture(fixture.Mesh);
                var mesh = new Mesh { name = "Interpolated" };
                _objects.Add(mesh);
                MeshChannelTransfer.ApplyInterpolated(snapshot, mesh, new[]
                {
                    MeshVertexSource.Copy(0),
                    MeshVertexSource.Copy(1),
                    MeshVertexSource.Midpoint(0, 1),
                });
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

                Assert.That(mesh.vertexCount, Is.EqualTo(3));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(mesh.bindposes.Length, Is.EqualTo(fixture.Mesh.bindposes.Length));
                using (var counts = mesh.GetBonesPerVertex())
                    Assert.That(counts[2], Is.GreaterThanOrEqualTo(1));
                using (var weights = mesh.GetAllBoneWeights())
                {
                    int offset = 0;
                    using (var counts = mesh.GetBonesPerVertex())
                    {
                        offset += counts[0] + counts[1];
                        float sum = 0f;
                        for (int i = 0; i < counts[2]; i++) sum += weights[offset + i].weight;
                        Assert.That(sum, Is.EqualTo(1f).Within(1e-5f));
                    }
                }
            }
        }

        [Test]
        public void CommandService_PreviewApplyAndStaleRevisionAreDeterministic()
        {
            var source = ModelingTestMeshFactory.CreateGrid(1, 1);
            var working = UnityEngine.Object.Instantiate(source);
            var document = ScriptableObject.CreateInstance<MeshEditDocument>();
            _objects.Add(document); _objects.Add(working); _objects.Add(source);
            document.Initialize(null, source, working);
            var operation = new TransformVerticesOperation(Matrix4x4.Translate(Vector3.forward));

            string hash;
            using (var preview = ModelingCommandService.Preview(document, operation, document.Revision)) hash = preview.PreviewHash;
            var before = document.Revision;
            var result = ModelingCommandService.Apply(document, operation, before, hash);

            Assert.That(document.Revision.Geometry, Is.EqualTo(before.Geometry + 1));
            Assert.That(document.ModifierStack, Has.Count.EqualTo(1));
            Assert.That(result.ResultHash, Is.EqualTo(hash));
            var stale = Assert.Throws<InvalidOperationException>(() =>
                ModelingCommandService.Preview(document, operation, before));
            StringAssert.Contains("STALE_MESH_REVISION", stale.Message);
        }

        [Test]
        public void CleanupAndWeld_RemoveOnlySafeRedundancy()
        {
            var source = new Mesh { name = "Dirty" };
            source.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,
                Vector3.zero, new Vector3(9f, 9f, 9f),
            };
            source.triangles = new[] { 0, 1, 2, 0, 3, 1 };
            var document = Document(source, out var working);
            var context = new MeshOperationContext(document, working, document.Revision, default);

            var weld = new WeldVerticesOperation(1e-6f, true).Apply(context);
            var cleanup = new CleanupMeshOperation().Apply(context);

            Assert.That(weld.RemovedVertexCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(cleanup.RemovedFaceCount + weld.RemovedFaceCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(working.vertexCount, Is.EqualTo(3));
            Assert.That(working.triangles.Length, Is.EqualTo(3));
        }

        [Test]
        public void MirrorAndSubdivide_PreserveRigAndBlendShapeChannels()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var document = Document(fixture.Mesh, out var working);
                var context = new MeshOperationContext(document, working, document.Revision, default);
                int sourceVertices = working.vertexCount;
                int sourceFaces = working.triangles.Length / 3;

                new MirrorMeshOperation(0, 0f, true).Apply(context);
                Assert.That(working.vertexCount, Is.EqualTo(sourceVertices * 2));
                Assert.That(working.blendShapeCount, Is.EqualTo(1));
                Assert.That(working.bindposes.Length, Is.EqualTo(fixture.Mesh.bindposes.Length));

                new SubdivideTrianglesOperation(new[] { 0 }).Apply(context);
                Assert.That(working.vertexCount, Is.EqualTo(sourceVertices * 2 + 1));
                Assert.That(working.triangles.Length / 3, Is.EqualTo(sourceFaces * 2 + 2));
                Assert.That(working.blendShapeCount, Is.EqualTo(1));
                using (var counts = working.GetBonesPerVertex()) Assert.That(counts.Length, Is.EqualTo(working.vertexCount));
            }
        }

        [Test]
        public void HalfEdgeTopology_DetectsDisconnectedComponentsAndBoundaries()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var topology = HalfEdgeTopology.Build(fixture.Mesh);
                Assert.That(topology.Faces, Has.Count.EqualTo(6));
                Assert.That(topology.HalfEdges, Has.Count.EqualTo(18));
                Assert.That(topology.ConnectedFaceComponents(), Has.Count.EqualTo(6));
                Assert.That(topology.BoundaryEdges, Has.Count.EqualTo(18));
                Assert.That(topology.BoundaryLoops(), Has.Count.EqualTo(6));
                Assert.That(topology.NonManifoldEdges, Is.Empty);
            }
        }

        [Test]
        public void BoundaryEdgeExtrude_PreservesOriginalVerticesAndCreatesSideFaces()
        {
            var source = ModelingTestMeshFactory.CreateGrid(1, 1);
            var document = Document(source, out var working);
            int originalVertices = working.vertexCount;
            int originalFaces = working.triangles.Length / 3;
            var operation = new ExtrudeBoundaryEdgesOperation(new[] { 0, 1 }, 0.25f);
            var validation = operation.Validate(new MeshOperationContext(document, working, document.Revision, default));
            Assert.That(validation.IsValid, Is.True);

            var result = operation.Apply(new MeshOperationContext(document, working, document.Revision, default));

            Assert.That(result.CreatedVertexCount, Is.EqualTo(2));
            Assert.That(result.CreatedFaceCount, Is.EqualTo(2));
            Assert.That(working.vertexCount, Is.EqualTo(originalVertices + 2));
            Assert.That(working.triangles.Length / 3, Is.EqualTo(originalFaces + 2));
        }

        [Test]
        public void Deform_LaplacianAndArapStayFiniteAndKeepTopology()
        {
            var source = ModelingTestMeshFactory.CreateGrid(4, 4);
            var document = Document(source, out var working);
            int[] indices = working.triangles;
            var parameters = new MeshDeformParameters
            {
                mode = MeshDeformMode.Laplacian,
                strength = 0.7f,
                radius = 10f,
                iterations = 5,
                direction = new[] { 0f, 0f, 1f },
                selectedVertices = new[] { 12 },
                pinnedVertices = new[] { 0, 4, 20, 24 },
            };
            var context = new MeshOperationContext(document, working, document.Revision, default);
            new DeformMeshOperation(parameters).Apply(context);
            parameters.mode = MeshDeformMode.Arap;
            new DeformMeshOperation(parameters).Apply(context);

            Assert.That(working.triangles, Is.EqualTo(indices));
            foreach (var vertex in working.vertices)
                Assert.That(float.IsNaN(vertex.x) || float.IsInfinity(vertex.x), Is.False);
        }

        [Test]
        public void Deform_SplineGuidesMeshAlongCurveWithoutChangingTopology()
        {
            var source = ModelingTestMeshFactory.CreateGrid(2, 4);
            var document = Document(source, out var working);
            var indices = working.triangles;
            var parameters = new MeshDeformParameters
            {
                mode = MeshDeformMode.Spline,
                axis = 1,
                strength = 1f,
                radius = 0f,
                splinePoints = new[] { 0f, 0f, 0f, 0f, 0.5f, 0.75f, 0f, 1f, 0f },
            };

            new DeformMeshOperation(parameters).Apply(
                new MeshOperationContext(document, working, document.Revision, default));

            Assert.That(working.triangles, Is.EqualTo(indices));
            float maximumZ = float.NegativeInfinity;
            foreach (var vertex in working.vertices) maximumZ = Mathf.Max(maximumZ, vertex.z);
            Assert.That(maximumZ, Is.GreaterThan(0.4f));
        }

        [Test]
        public void SelectionState_PersistsRegionsLinksAndConstraintsUntilTopologyChanges()
        {
            var source = ModelingTestMeshFactory.CreateGrid(1, 1);
            var document = Document(source, out _);
            document.Selection.SetSelection(MeshElementKind.Face, new[] { 0, 1 }, document.Revision.Topology);
            document.Selection.SaveRegion("AllFaces");
            document.Selection.SetSelection(MeshElementKind.Face, new[] { 0 }, document.Revision.Topology);
            document.Selection.SaveRegion("Anchor");
            document.Selection.Link("fit", "Anchor", "AllFaces", 0.75f, new Vector3(1f, 0f, 1f));
            document.Selection.SetConstraint("Anchor", 1f, 0.8f, true);

            Assert.That(document.Selection.SelectRegion("AllFaces", document.Revision.Topology), Is.True);
            Assert.That(document.Selection.Indices, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(document.Selection.Links, Has.Count.EqualTo(1));
            Assert.That(document.Selection.Links[0].AxisMask, Is.EqualTo(new Vector3(1f, 0f, 1f)));
            Assert.That(document.Selection.Constraints[0].Pinned, Is.True);

            document.AdvanceTopology();
            Assert.That(document.Selection.Regions, Is.Empty);
            Assert.That(document.Selection.Indices, Is.Empty);
        }

        [Test]
        public void MergeStaticMeshes_CompactsIdenticalMaterialSlots()
        {
            string folder = $"Assets/__AssetModelerAdvanced_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(folder);
            _assetFolders.Add(folder);
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            _objects.Add(material);
            var roots = new List<GameObject>();
            for (int i = 0; i < 2; i++)
            {
                var root = new GameObject("Merge_" + i);
                root.transform.position = Vector3.right * i;
                var mesh = ModelingTestMeshFactory.CreateGrid(1, 1);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = material;
                roots.Add(root);
                _objects.Add(root);
                _objects.Add(mesh);
            }
            roots[1].transform.SetParent(roots[0].transform, true);

            var merged = SceneObjectOperations.MergeStaticMeshes(roots, folder, true);
            _objects.Add(merged);
            var mergedMesh = merged.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(mergedMesh.subMeshCount, Is.EqualTo(1));
            Assert.That(merged.GetComponent<MeshRenderer>().sharedMaterials, Has.Length.EqualTo(1));
            Assert.That(mergedMesh.triangles, Has.Length.EqualTo(12));
        }

        [Test]
        public void TriangleBvh_ReturnsNearestSurfacePoint()
        {
            var mesh = ModelingTestMeshFactory.CreateGrid(1, 1);
            _objects.Add(mesh);
            var bvh = new TriangleBvh(mesh, Matrix4x4.identity);
            var result = bvh.FindClosest(new Vector3(0.25f, 0.25f, 2f), 5f);
            Assert.That(result.Found, Is.True);
            Assert.That(result.Point.z, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(result.Distance, Is.EqualTo(2f).Within(1e-5f));
        }

        [Test]
        public void SurfaceFit_ProjectsClothingToConfiguredClearance()
        {
            var body = new GameObject("BodyTarget");
            var clothing = new GameObject("Clothing");
            _objects.Add(body); _objects.Add(clothing);
            var bodyMesh = ModelingTestMeshFactory.CreateGrid(2, 2);
            var clothingMesh = UnityEngine.Object.Instantiate(bodyMesh);
            _objects.Add(bodyMesh); _objects.Add(clothingMesh);
            var clothingVertices = clothingMesh.vertices;
            for (int i = 0; i < clothingVertices.Length; i++) clothingVertices[i].z = 0.25f;
            clothingMesh.vertices = clothingVertices;
            clothingMesh.RecalculateBounds();
            body.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            var bodyRenderer = body.AddComponent<MeshRenderer>();
            clothing.AddComponent<MeshFilter>().sharedMesh = clothingMesh;
            var clothingRenderer = clothing.AddComponent<MeshRenderer>();
            var document = ScriptableObject.CreateInstance<MeshEditDocument>();
            _objects.Add(document);
            document.Initialize(clothing, clothingMesh, clothingMesh);
            var parameters = SmartSurfaceFitOperation.CreateParameters(
                clothingRenderer,
                new[] { bodyRenderer },
                0.05f,
                1f);
            parameters.shapePreservation = 0f;
            parameters.iterations = 2;
            new SmartSurfaceFitOperation(parameters).Apply(
                new MeshOperationContext(document, clothingMesh, document.Revision, default));

            Assert.That(clothingMesh.vertexCount, Is.GreaterThan(bodyMesh.vertexCount), "Surface Fit must refine sparse clothing automatically.");
            foreach (var vertex in clothingMesh.vertices)
                Assert.That(vertex.z, Is.GreaterThanOrEqualTo(0.049f));
            var poseReport = SurfaceFitPoseEvaluator.EvaluateCurrentPose(
                clothingRenderer, new[] { bodyRenderer }, 0.049f, 1f);
            Assert.That(poseReport.SampleCount, Is.EqualTo(1));
            Assert.That(poseReport.IsValid, Is.True);
        }

        [Test]
        public void ExternalAssetStaging_CopiesAndValidatesMeshAsset()
        {
            string root = $"Assets/__AssetModelerStaging_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(root);
            _assetFolders.Add(root);
            var mesh = ModelingTestMeshFactory.CreateGrid(1, 1);
            string sourcePath = root + "/Source.asset";
            AssetDatabase.CreateAsset(mesh, sourcePath);

            var result = AssetModelerStagingService.Stage(sourcePath, root + "/Validated");

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.MeshCount, Is.EqualTo(1));
            Assert.That(result.TriangleCount, Is.EqualTo(2));
            Assert.That(result.AssetPath, Is.Not.EqualTo(sourcePath));
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(result.AssetPath), Is.Not.Null);
        }

        [Test]
        public void SmartSplit_ModularPresetClassifiesSixteenRiggedRegions()
        {
            using (var fixture = ModelingTestMeshFactory.CreateModularCharacter())
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root, SmartBodySplitPreset.Modular);
                Assert.That(proposal.Parts, Has.Count.EqualTo(16));
                Assert.That(proposal.SourceTriangleCount, Is.EqualTo(16));
                Assert.That(SmartBodySplitPresets.Id(proposal.Preset), Is.EqualTo("modular"));
                foreach (var part in SmartBodySplitPresets.Parts(SmartBodySplitPreset.Modular))
                    Assert.That(proposal.Find(part)?.Triangles.Count, Is.EqualTo(1), part.ToString());
            }
        }

        [Test]
        public void SmartSplit_ClosesCreatedCutLoopsWithRealCapGeometry()
        {
            string folder = $"Assets/__AssetModelerCaps_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(folder);
            _assetFolders.Add(folder);
            var root = new GameObject("ClosedOctahedron");
            _objects.Add(root);
            var mesh = new Mesh { name = "ClosedOctahedron" };
            _objects.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 1f),
                new Vector3(-1f, 0f, 0f),
                new Vector3(0f, 0f, -1f),
            };
            mesh.triangles = new[]
            {
                0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 2, 5,
                1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            var proposal = new SmartBodySplitProposal
            {
                Target = root,
                SourceRenderer = renderer,
                SourceMesh = mesh,
                SourceHash = MeshFingerprint.Compute(mesh),
                RigType = "None",
                Preset = SmartBodySplitPreset.Core,
            };
            var upper = new SmartBodySplitPartProposal(SemanticBodyPart.Head);
            var lower = new SmartBodySplitPartProposal(SemanticBodyPart.Torso);
            proposal.Parts.Add(upper);
            proposal.Parts.Add(lower);
            var indices = mesh.triangles;
            for (int triangle = 0; triangle < 8; triangle++)
            {
                var destination = triangle < 4 ? upper : lower;
                int offset = triangle * 3;
                destination.Triangles.Add(new SourceTriangle(
                    0, triangle, indices[offset], indices[offset + 1], indices[offset + 2], 1f));
            }
            proposal.FinalizeProposal();

            var result = SmartBodySplitService.Apply(
                proposal, folder, proposal.SourceHash, proposal.PreviewHash, true);
            _objects.Add(result.PartsRoot);

            Assert.That(result.Validation.IsValid, Is.True);
            Assert.That(result.Manifest.Parts, Has.Count.EqualTo(2));
            foreach (var part in result.Manifest.Parts)
            {
                Assert.That(part.SourceTriangleCount, Is.EqualTo(4));
                Assert.That(part.CapLoopCount, Is.EqualTo(1));
                Assert.That(part.CapTriangleCount, Is.EqualTo(2));
                Assert.That(part.Mesh.vertexCount, Is.EqualTo(5), "Caps must reuse boundary vertices and therefore preserve all channels exactly.");
                Assert.That(part.Mesh.triangles.Length / 3, Is.EqualTo(6));
                Assert.That(HalfEdgeTopology.Build(part.Mesh).BoundaryEdges, Is.Empty, part.SlotId);
            }
            Assert.That(mesh.triangles.Length / 3, Is.EqualTo(8), "Source mesh must remain untouched.");
        }

        [Test]
        public void SmartSplit_ClosesCutLoopAcrossDuplicatedFbxSeamVertices()
        {
            string folder = $"Assets/__AssetModelerSeamCaps_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(folder);
            _assetFolders.Add(folder);
            var root = new GameObject("SeamedOctahedron");
            _objects.Add(root);
            var mesh = new Mesh { name = "SeamedOctahedron" };
            _objects.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 1f),
                new Vector3(-1f, 0f, 0f),
                new Vector3(0f, 0f, -1f),
                new Vector3(1f, 0f, 0f), // FBX UV/normal seam duplicate of vertex 2.
            };
            mesh.triangles = new[]
            {
                0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 6, 5,
                1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2,
            };
            mesh.uv = new[]
            {
                new Vector2(0.5f, 1f), new Vector2(0.5f, 0f),
                new Vector2(1f, 0.5f), new Vector2(0.75f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(0.25f, 0.5f),
                new Vector2(0f, 0.5f),
            };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.right) };
            var boneWeights = new BoneWeight[mesh.vertexCount];
            for (int vertex = 0; vertex < boneWeights.Length; vertex++)
                boneWeights[vertex] = new BoneWeight { boneIndex0 = vertex % 2, weight0 = 1f };
            mesh.boneWeights = boneWeights;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var blendVertices = new Vector3[mesh.vertexCount];
            var blendNormals = new Vector3[mesh.vertexCount];
            var blendTangents = new Vector3[mesh.vertexCount];
            for (int vertex = 0; vertex < blendVertices.Length; vertex++)
                blendVertices[vertex] = Vector3.up * (vertex + 1) * 0.01f;
            mesh.AddBlendShapeFrame("Pulse", 100f, blendVertices, blendNormals, blendTangents);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            var proposal = new SmartBodySplitProposal
            {
                Target = root,
                SourceRenderer = renderer,
                SourceMesh = mesh,
                SourceHash = MeshFingerprint.Compute(mesh),
                RigType = "None",
                Preset = SmartBodySplitPreset.Core,
            };
            var upper = new SmartBodySplitPartProposal(SemanticBodyPart.Head);
            var lower = new SmartBodySplitPartProposal(SemanticBodyPart.Torso);
            proposal.Parts.Add(upper);
            proposal.Parts.Add(lower);
            var indices = mesh.triangles;
            for (int triangle = 0; triangle < 8; triangle++)
            {
                var destination = triangle < 4 ? upper : lower;
                int offset = triangle * 3;
                destination.Triangles.Add(new SourceTriangle(
                    0, triangle, indices[offset], indices[offset + 1], indices[offset + 2], 1f));
            }
            proposal.FinalizeProposal();

            var result = SmartBodySplitService.Apply(
                proposal, folder, proposal.SourceHash, proposal.PreviewHash, true);
            _objects.Add(result.PartsRoot);

            Assert.That(result.Validation.IsValid, Is.True);
            SmartBodySplitManifestPart seamedPart = null;
            for (int i = 0; i < result.Manifest.Parts.Count; i++)
                if (result.Manifest.Parts[i].CapTriangleCount == 4) seamedPart = result.Manifest.Parts[i];
            Assert.That(seamedPart, Is.Not.Null, "The UV seam must select the interpolated fan fallback.");
            Assert.That(seamedPart.CapLoopCount, Is.EqualTo(1));
            Assert.That(seamedPart.Mesh.vertexCount, Is.EqualTo(7));
            Assert.That(seamedPart.Mesh.triangles.Length / 3, Is.EqualTo(8));
            Assert.That(seamedPart.SourceVertexIndices, Does.Contain(-1), "Generated cap centers must not impersonate source vertices.");
            Assert.That(seamedPart.Mesh.uv.Length, Is.EqualTo(seamedPart.Mesh.vertexCount));
            Assert.That(seamedPart.Mesh.blendShapeCount, Is.EqualTo(1));
            int capCenter = -1;
            for (int vertex = 0; vertex < seamedPart.SourceVertexIndices.Count; vertex++)
                if (seamedPart.SourceVertexIndices[vertex] < 0) { capCenter = vertex; break; }
            var capSnapshot = MeshChannelSnapshot.Capture(seamedPart.Mesh);
            int weightStart = capSnapshot.BoneWeightOffsets[capCenter];
            int weightEnd = capSnapshot.BoneWeightOffsets[capCenter + 1];
            float weightSum = 0f;
            for (int weightIndex = weightStart; weightIndex < weightEnd; weightIndex++)
                weightSum += capSnapshot.BoneWeights[weightIndex].weight;
            Assert.That(weightEnd - weightStart, Is.EqualTo(2), "The generated center must interpolate both rig influences.");
            Assert.That(weightSum, Is.EqualTo(1f).Within(SplitFidelityValidator.BoneWeightTolerance));
            Assert.That(mesh.vertexCount, Is.EqualTo(7), "Source mesh must remain untouched.");
        }

        [Test]
        public void SmartSplit_DecomposesDegreeFourTouchingCutLoops()
        {
            string folder = $"Assets/__AssetModelerBranchCaps_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(folder);
            _assetFolders.Add(folder);
            var root = new GameObject("TouchingOctahedra");
            _objects.Add(root);
            var mesh = new Mesh { name = "TouchingOctahedra" };
            _objects.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, -1f),
                new Vector3(2f, 1f, 0f), new Vector3(2f, -1f, 0f),
                new Vector3(3f, 0f, 0f), new Vector3(2f, 0f, 1f),
                new Vector3(1f, 0f, 0f), // Distinct shell touching vertex 2 geometrically.
                new Vector3(2f, 0f, -1f),
            };
            mesh.triangles = new[]
            {
                0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 2, 5,
                1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2,
                6, 9, 8, 6, 10, 9, 6, 11, 10, 6, 8, 11,
                7, 8, 9, 7, 9, 10, 7, 10, 11, 7, 11, 8,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            var proposal = new SmartBodySplitProposal
            {
                Target = root,
                SourceRenderer = renderer,
                SourceMesh = mesh,
                SourceHash = MeshFingerprint.Compute(mesh),
                RigType = "None",
                Preset = SmartBodySplitPreset.Core,
            };
            var upper = new SmartBodySplitPartProposal(SemanticBodyPart.Head);
            var lower = new SmartBodySplitPartProposal(SemanticBodyPart.Torso);
            proposal.Parts.Add(upper);
            proposal.Parts.Add(lower);
            var indices = mesh.triangles;
            for (int triangle = 0; triangle < 16; triangle++)
            {
                var destination = triangle % 8 < 4 ? upper : lower;
                int offset = triangle * 3;
                destination.Triangles.Add(new SourceTriangle(
                    0, triangle, indices[offset], indices[offset + 1], indices[offset + 2], 1f));
            }
            proposal.FinalizeProposal();

            var result = SmartBodySplitService.Apply(
                proposal, folder, proposal.SourceHash, proposal.PreviewHash, true);
            _objects.Add(result.PartsRoot);

            Assert.That(result.Validation.IsValid, Is.True);
            Assert.That(result.Manifest.Parts, Has.Count.EqualTo(2));
            foreach (var part in result.Manifest.Parts)
            {
                Assert.That(part.SourceTriangleCount, Is.EqualTo(8));
                Assert.That(part.CapLoopCount, Is.EqualTo(2));
                Assert.That(part.CapTriangleCount, Is.EqualTo(4));
                Assert.That(part.Mesh.vertexCount, Is.EqualTo(10));
                Assert.That(part.Mesh.triangles.Length / 3, Is.EqualTo(12));
                Assert.That(HalfEdgeTopology.Build(part.Mesh).BoundaryEdges, Is.Empty, part.SlotId);
            }
            Assert.That(mesh.triangles.Length / 3, Is.EqualTo(16), "Source mesh must remain untouched.");
        }

        private MeshEditDocument Document(Mesh source, out Mesh working)
        {
            working = UnityEngine.Object.Instantiate(source);
            working.name = source.name + "_Working";
            var document = ScriptableObject.CreateInstance<MeshEditDocument>();
            document.Initialize(null, source, working);
            _objects.Add(source); _objects.Add(working); _objects.Add(document);
            return document;
        }
    }
}
