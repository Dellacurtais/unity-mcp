using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    [Serializable]
    public sealed class MeshChannelManifest
    {
        [SerializeField] private int _vertexCount;
        [SerializeField] private int _subMeshCount;
        [SerializeField] private IndexFormat _indexFormat;
        [SerializeField] private bool _normals;
        [SerializeField] private bool _tangents;
        [SerializeField] private bool _colors;
        [SerializeField] private int[] _uvDimensions = new int[8];
        [SerializeField] private bool _boneWeights;
        [SerializeField] private int _bindPoseCount;
        [SerializeField] private int _blendShapeCount;
        [SerializeField] private VertexAttributeDescriptor[] _vertexAttributes = Array.Empty<VertexAttributeDescriptor>();

        public int VertexCount => _vertexCount;
        public int SubMeshCount => _subMeshCount;
        public IndexFormat IndexFormat => _indexFormat;
        public bool HasNormals => _normals;
        public bool HasTangents => _tangents;
        public bool HasColors => _colors;
        public bool HasBoneWeights => _boneWeights;
        public int BindPoseCount => _bindPoseCount;
        public int BlendShapeCount => _blendShapeCount;
        public IReadOnlyList<int> UvDimensions => _uvDimensions;
        public IReadOnlyList<VertexAttributeDescriptor> VertexAttributes => _vertexAttributes;

        public int GetUvDimension(int channel)
        {
            if (channel < 0 || channel >= _uvDimensions.Length) throw new ArgumentOutOfRangeException(nameof(channel));
            return _uvDimensions[channel];
        }

        public static MeshChannelManifest Capture(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var manifest = new MeshChannelManifest
            {
                _vertexCount = mesh.vertexCount,
                _subMeshCount = mesh.subMeshCount,
                _indexFormat = mesh.indexFormat,
                _normals = mesh.HasVertexAttribute(VertexAttribute.Normal),
                _tangents = mesh.HasVertexAttribute(VertexAttribute.Tangent),
                _colors = mesh.HasVertexAttribute(VertexAttribute.Color),
                _boneWeights = mesh.HasVertexAttribute(VertexAttribute.BlendWeight)
                    || mesh.HasVertexAttribute(VertexAttribute.BlendIndices),
                _bindPoseCount = mesh.bindposes != null ? mesh.bindposes.Length : 0,
                _blendShapeCount = mesh.blendShapeCount,
                _vertexAttributes = mesh.GetVertexAttributes(),
            };
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                manifest._uvDimensions[channel] = mesh.HasVertexAttribute(attribute)
                    ? mesh.GetVertexAttributeDimension(attribute)
                    : 0;
            }
            return manifest;
        }
    }
}
