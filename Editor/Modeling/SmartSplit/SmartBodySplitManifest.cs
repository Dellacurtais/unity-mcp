using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    [Serializable]
    public sealed class SmartBodySplitManifestPart
    {
        [SerializeField] private string _slotId;
        [SerializeField] private string _label;
        [SerializeField] private Mesh _mesh;
        [SerializeField] private string _assetPath;
        [SerializeField] private int _sourceTriangleCount;
        [SerializeField] private int[] _sourceVertexIndices = Array.Empty<int>();
        [SerializeField] private float _confidence;

        public string SlotId => _slotId;
        public string Label => _label;
        public Mesh Mesh => _mesh;
        public string AssetPath => _assetPath;
        public int SourceTriangleCount => _sourceTriangleCount;
        public IReadOnlyList<int> SourceVertexIndices => _sourceVertexIndices;
        public float Confidence => _confidence;

        public void Configure(
            string slotId,
            string label,
            Mesh mesh,
            string assetPath,
            int sourceTriangleCount,
            int[] sourceVertexIndices,
            float confidence)
        {
            _slotId = slotId ?? string.Empty;
            _label = label ?? _slotId;
            _mesh = mesh;
            _assetPath = assetPath ?? string.Empty;
            _sourceTriangleCount = Mathf.Max(0, sourceTriangleCount);
            _sourceVertexIndices = sourceVertexIndices != null ? (int[])sourceVertexIndices.Clone() : Array.Empty<int>();
            _confidence = Mathf.Clamp01(confidence);
        }
    }

    public sealed class SmartBodySplitManifest : ScriptableObject
    {
        [SerializeField] private string _splitId;
        [SerializeField] private string _sourceHash;
        [SerializeField] private string _previewHash;
        [SerializeField] private string _rigType;
        [SerializeField] private SmartBodySplitPreset _preset;
        [SerializeField] private bool _valid;
        [SerializeField] private int _sourceTriangleCount;
        [SerializeField] private string[] _validationMessages = Array.Empty<string>();
        [SerializeField] private List<SmartBodySplitManifestPart> _parts = new List<SmartBodySplitManifestPart>();

        public string SplitId => _splitId;
        public string SourceHash => _sourceHash;
        public string PreviewHash => _previewHash;
        public string RigType => _rigType;
        public SmartBodySplitPreset Preset => _preset;
        public bool IsValid => _valid;
        public int SourceTriangleCount => _sourceTriangleCount;
        public IReadOnlyList<string> ValidationMessages => _validationMessages;
        public IReadOnlyList<SmartBodySplitManifestPart> Parts => _parts;

        public void Configure(
            string splitId,
            SmartBodySplitProposal proposal,
            bool valid,
            IEnumerable<string> validationMessages,
            IEnumerable<SmartBodySplitManifestPart> parts)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            _splitId = splitId ?? string.Empty;
            _sourceHash = proposal.SourceHash ?? string.Empty;
            _previewHash = proposal.PreviewHash ?? string.Empty;
            _rigType = proposal.RigType ?? "None";
            _preset = proposal.Preset;
            _valid = valid;
            _sourceTriangleCount = proposal.SourceTriangleCount;
            _validationMessages = validationMessages != null
                ? new List<string>(validationMessages).ToArray()
                : Array.Empty<string>();
            _parts = parts != null
                ? new List<SmartBodySplitManifestPart>(parts)
                : new List<SmartBodySplitManifestPart>();
        }
    }
}
