using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology
{
    [Serializable]
    internal sealed class AdaptiveRefineParameters
    {
        public float maximumEdgeLength;
    }

    public sealed class AdaptiveRefineOperation : IRecordedMeshOperation
    {
        private readonly float _maximumEdgeLength;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.adaptive_refine", "Adaptive boundary refinement", true, true, true, true, true);

        public AdaptiveRefineOperation(float maximumEdgeLength)
        {
            _maximumEdgeLength = maximumEdgeLength;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_maximumEdgeLength <= 0f || float.IsNaN(_maximumEdgeLength) || float.IsInfinity(_maximumEdgeLength))
                report.Error("REFINE_LENGTH_INVALID", "Maximum edge length must be finite and positive.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var positions = context.WorkingMesh.vertices;
            var topology = HalfEdgeTopology.Build(context.WorkingMesh);
            float squared = _maximumEdgeLength * _maximumEdgeLength;
            var faces = new List<int>();
            for (int i = 0; i < topology.Faces.Count; i++)
            {
                if ((i & 4095) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                var face = topology.Faces[i];
                if ((positions[face.A] - positions[face.B]).sqrMagnitude > squared
                    || (positions[face.B] - positions[face.C]).sqrMagnitude > squared
                    || (positions[face.C] - positions[face.A]).sqrMagnitude > squared)
                    faces.Add(face.Index);
            }
            if (faces.Count == 0) return new MeshOperationResult();
            return new SubdivideTrianglesOperation(faces).Apply(context);
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new AdaptiveRefineParameters { maximumEdgeLength = _maximumEdgeLength });

        public static AdaptiveRefineOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<AdaptiveRefineParameters>(json ?? "{}") ?? new AdaptiveRefineParameters();
            return new AdaptiveRefineOperation(value.maximumEdgeLength);
        }
    }
}
