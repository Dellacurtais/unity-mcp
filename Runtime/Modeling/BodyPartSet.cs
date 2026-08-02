using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [Serializable]
    public sealed class SeamAnchorDefinition
    {
        [SerializeField] private int _sourceVertexIndex;
        [SerializeField] private Vector3 _position;
        [SerializeField] private Vector3 _normal;

        public int SourceVertexIndex => _sourceVertexIndex;
        public Vector3 Position => _position;
        public Vector3 Normal => _normal;

        public void Configure(int sourceVertexIndex, Vector3 position, Vector3 normal)
        {
            _sourceVertexIndex = sourceVertexIndex;
            _position = position;
            _normal = normal;
        }
    }

    [Serializable]
    public sealed class SkeletonCompatibilitySignature
    {
        [SerializeField] private string _rigType;
        [SerializeField] private string _hierarchyHash;
        [SerializeField] private string _bindPoseHash;
        [SerializeField] private int _boneCount;

        public string RigType => _rigType;
        public string HierarchyHash => _hierarchyHash;
        public string BindPoseHash => _bindPoseHash;
        public int BoneCount => _boneCount;

        public void Configure(string rigType, string hierarchyHash, string bindPoseHash, int boneCount)
        {
            _rigType = rigType ?? "None";
            _hierarchyHash = hierarchyHash ?? string.Empty;
            _bindPoseHash = bindPoseHash ?? string.Empty;
            _boneCount = Mathf.Max(0, boneCount);
        }

        public bool IsCompatibleWith(SkeletonCompatibilitySignature other)
        {
            if (other == null) return false;
            return _boneCount == other._boneCount
                && string.Equals(_rigType, other._rigType, StringComparison.Ordinal)
                && string.Equals(_hierarchyHash, other._hierarchyHash, StringComparison.Ordinal)
                && string.Equals(_bindPoseHash, other._bindPoseHash, StringComparison.Ordinal);
        }
    }

    [Serializable]
    public sealed class BodyPartDefinition
    {
        [SerializeField] private string _slotId;
        [SerializeField] private string _label;
        [SerializeField] private Mesh _mesh;
        [SerializeField] private Material[] _materials = Array.Empty<Material>();
        [SerializeField] private int _sourceTriangleCount;
        [SerializeField] private int _sourceVertexCount;
        [SerializeField] private float _confidence;
        [SerializeField] private string _seamSignature;
        [SerializeField] private SeamAnchorDefinition[] _seamAnchors = Array.Empty<SeamAnchorDefinition>();
        [SerializeField] private int _capTriangleCount;
        [SerializeField] private int _capLoopCount;

        public string SlotId => _slotId;
        public string Label => _label;
        public Mesh Mesh => _mesh;
        public IReadOnlyList<Material> Materials => _materials;
        public int SourceTriangleCount => _sourceTriangleCount;
        public int SourceVertexCount => _sourceVertexCount;
        public float Confidence => _confidence;
        public string SeamSignature => _seamSignature;
        public IReadOnlyList<SeamAnchorDefinition> SeamAnchors => _seamAnchors;
        public int CapTriangleCount => _capTriangleCount;
        public int CapLoopCount => _capLoopCount;

        public void Configure(
            string slotId,
            string label,
            Mesh mesh,
            Material[] materials,
            int sourceTriangleCount,
            int sourceVertexCount,
            float confidence,
            string seamSignature = null,
            SeamAnchorDefinition[] seamAnchors = null,
            int capTriangleCount = 0,
            int capLoopCount = 0)
        {
            _slotId = slotId ?? string.Empty;
            _label = label ?? _slotId;
            _mesh = mesh;
            _materials = materials != null ? (Material[])materials.Clone() : Array.Empty<Material>();
            _sourceTriangleCount = Mathf.Max(0, sourceTriangleCount);
            _sourceVertexCount = Mathf.Max(0, sourceVertexCount);
            _confidence = Mathf.Clamp01(confidence);
            _seamSignature = seamSignature ?? string.Empty;
            _seamAnchors = seamAnchors != null ? (SeamAnchorDefinition[])seamAnchors.Clone() : Array.Empty<SeamAnchorDefinition>();
            _capTriangleCount = Mathf.Max(0, capTriangleCount);
            _capLoopCount = Mathf.Max(0, capLoopCount);
        }

        public bool IsSeamCompatibleWith(BodyPartDefinition other)
            => other != null
                && !string.IsNullOrEmpty(_seamSignature)
                && string.Equals(_seamSignature, other._seamSignature, StringComparison.Ordinal);
    }

    [CreateAssetMenu(fileName = "BodyPartSet", menuName = "Unity MCP/Modeling/Body Part Set")]
    public sealed class BodyPartSet : ScriptableObject
    {
        [SerializeField] private string _sourceAssetGuid;
        [SerializeField] private string _sourceHash;
        [SerializeField] private string _splitId;
        [SerializeField] private SkeletonCompatibilitySignature _skeleton = new SkeletonCompatibilitySignature();
        [SerializeField] private List<BodyPartDefinition> _parts = new List<BodyPartDefinition>();

        public string SourceAssetGuid => _sourceAssetGuid;
        public string SourceHash => _sourceHash;
        public string SplitId => _splitId;
        public SkeletonCompatibilitySignature Skeleton => _skeleton;
        public IReadOnlyList<BodyPartDefinition> Parts => _parts;

        public void Configure(
            string sourceAssetGuid,
            string sourceHash,
            string splitId,
            SkeletonCompatibilitySignature skeleton,
            IEnumerable<BodyPartDefinition> parts)
        {
            _sourceAssetGuid = sourceAssetGuid ?? string.Empty;
            _sourceHash = sourceHash ?? string.Empty;
            _splitId = splitId ?? string.Empty;
            _skeleton = skeleton ?? new SkeletonCompatibilitySignature();
            _parts = parts != null ? new List<BodyPartDefinition>(parts) : new List<BodyPartDefinition>();
        }

        public BodyPartDefinition FindPart(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return null;
            for (int i = 0; i < _parts.Count; i++)
                if (string.Equals(_parts[i]?.SlotId, slotId, StringComparison.Ordinal)) return _parts[i];
            return null;
        }
    }
}
