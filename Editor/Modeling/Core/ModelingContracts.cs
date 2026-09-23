using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public enum MeshElementKind
    {
        Vertex,
        Edge,
        Face,
        Region,
    }

    [Serializable]
    public struct StableElementId : IEquatable<StableElementId>
    {
        [SerializeField] private MeshElementKind _kind;
        [SerializeField] private ulong _value;

        public MeshElementKind Kind => _kind;
        public ulong Value => _value;
        public bool IsValid => _value != 0;

        public StableElementId(MeshElementKind kind, ulong value)
        {
            _kind = kind;
            _value = value;
        }

        public bool Equals(StableElementId other) => _kind == other._kind && _value == other._value;
        public override bool Equals(object obj) => obj is StableElementId other && Equals(other);
        public override int GetHashCode() => unchecked(((int)_kind * 397) ^ _value.GetHashCode());
        public override string ToString() => $"{_kind}:{_value}";
    }

    [Serializable]
    public struct MeshRevision : IEquatable<MeshRevision>
    {
        [SerializeField] private uint _topology;
        [SerializeField] private uint _geometry;

        public uint Topology => _topology;
        public uint Geometry => _geometry;

        public MeshRevision(uint topology, uint geometry)
        {
            _topology = topology;
            _geometry = geometry;
        }

        public MeshRevision NextTopology() => new MeshRevision(checked(_topology + 1), checked(_geometry + 1));
        public MeshRevision NextGeometry() => new MeshRevision(_topology, checked(_geometry + 1));
        public bool Equals(MeshRevision other) => _topology == other._topology && _geometry == other._geometry;
        public override bool Equals(object obj) => obj is MeshRevision other && Equals(other);
        public override int GetHashCode() => unchecked(((int)_topology * 397) ^ (int)_geometry);
        public override string ToString() => $"t{_topology}:g{_geometry}";
    }

    public enum MeshChannelPolicy
    {
        Preserve,
        Interpolate,
        Recalculate,
        Drop,
    }

    [Serializable]
    public sealed class MeshValidationIssue
    {
        [SerializeField] private string _code;
        [SerializeField] private string _message;
        [SerializeField] private bool _error;

        public string Code => _code;
        public string Message => _message;
        public bool IsError => _error;

        public MeshValidationIssue(string code, string message, bool error)
        {
            _code = code ?? "UNKNOWN";
            _message = message ?? string.Empty;
            _error = error;
        }
    }

    [Serializable]
    public sealed class MeshValidationReport
    {
        [SerializeField] private List<MeshValidationIssue> _issues = new List<MeshValidationIssue>();

        public IReadOnlyList<MeshValidationIssue> Issues => _issues;
        public bool IsValid
        {
            get
            {
                for (int i = 0; i < _issues.Count; i++)
                    if (_issues[i] != null && _issues[i].IsError) return false;
                return true;
            }
        }

        public void Error(string code, string message) => _issues.Add(new MeshValidationIssue(code, message, true));
        public void Warning(string code, string message) => _issues.Add(new MeshValidationIssue(code, message, false));
    }

    [Serializable]
    public sealed class MeshOperationDescriptor
    {
        [SerializeField] private string _id;
        [SerializeField] private string _displayName;
        [SerializeField] private bool _changesTopology;
        [SerializeField] private bool _supportsSkinnedMesh;
        [SerializeField] private bool _supportsBlendShapes;
        [SerializeField] private bool _requiresPreview;
        [SerializeField] private bool _cancelable;

        public string Id => _id;
        public string DisplayName => _displayName;
        public bool ChangesTopology => _changesTopology;
        public bool SupportsSkinnedMesh => _supportsSkinnedMesh;
        public bool SupportsBlendShapes => _supportsBlendShapes;
        public bool RequiresPreview => _requiresPreview;
        public bool Cancelable => _cancelable;

        public MeshOperationDescriptor(
            string id,
            string displayName,
            bool changesTopology,
            bool supportsSkinnedMesh,
            bool supportsBlendShapes,
            bool requiresPreview,
            bool cancelable)
        {
            _id = id ?? throw new ArgumentNullException(nameof(id));
            _displayName = displayName ?? id;
            _changesTopology = changesTopology;
            _supportsSkinnedMesh = supportsSkinnedMesh;
            _supportsBlendShapes = supportsBlendShapes;
            _requiresPreview = requiresPreview;
            _cancelable = cancelable;
        }
    }

    public sealed class MeshOperationContext
    {
        public MeshEditDocument Document { get; }
        public Mesh WorkingMesh { get; }
        public MeshRevision ExpectedRevision { get; }
        public CancellationToken CancellationToken { get; }

        public MeshOperationContext(
            MeshEditDocument document,
            Mesh workingMesh,
            MeshRevision expectedRevision,
            CancellationToken cancellationToken)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            WorkingMesh = workingMesh ?? throw new ArgumentNullException(nameof(workingMesh));
            ExpectedRevision = expectedRevision;
            CancellationToken = cancellationToken;
        }
    }

    public sealed class MeshOperationResult
    {
        public string OperationId { get; set; }
        public MeshRevision RevisionBefore { get; set; }
        public MeshRevision RevisionAfter { get; set; }
        public string InputHash { get; set; }
        public string ResultHash { get; set; }
        public MeshValidationReport Validation { get; set; }
        public int CreatedVertexCount { get; set; }
        public int RemovedVertexCount { get; set; }
        public int CreatedFaceCount { get; set; }
        public int RemovedFaceCount { get; set; }
        public int RemovedLineCount { get; set; }
        public int RemovedPointCount { get; set; }
        public int RemovedComponentCount { get; set; }
    }

    public interface IMeshOperation
    {
        MeshOperationDescriptor Descriptor { get; }
        MeshValidationReport Validate(MeshOperationContext context);
        MeshOperationResult Preview(MeshOperationContext context);
        MeshOperationResult Apply(MeshOperationContext context);
    }
}
