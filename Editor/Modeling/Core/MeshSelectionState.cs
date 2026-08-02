using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    [Serializable]
    public sealed class MeshNamedRegion
    {
        [SerializeField] private string _name;
        [SerializeField] private MeshElementKind _kind;
        [SerializeField] private List<int> _indices = new List<int>();

        public string Name => _name;
        public MeshElementKind Kind => _kind;
        public IReadOnlyList<int> Indices => _indices;

        internal MeshNamedRegion(string name, MeshElementKind kind, IEnumerable<int> indices)
        {
            _name = name;
            _kind = kind;
            Replace(indices);
        }

        internal void Replace(IEnumerable<int> indices)
        {
            _indices.Clear();
            if (indices == null) return;
            var unique = new SortedSet<int>();
            foreach (int index in indices) if (index >= 0) unique.Add(index);
            _indices.AddRange(unique);
        }
    }

    [Serializable]
    public sealed class MeshRegionLink
    {
        [SerializeField] private string _name;
        [SerializeField] private string _sourceRegion;
        [SerializeField] private string _targetRegion;
        [SerializeField] private float _stiffness;
        [SerializeField] private Vector3 _axisMask = Vector3.one;

        public string Name => _name;
        public string SourceRegion => _sourceRegion;
        public string TargetRegion => _targetRegion;
        public float Stiffness => _stiffness;
        public Vector3 AxisMask => _axisMask;

        internal MeshRegionLink(string name, string sourceRegion, string targetRegion, float stiffness, Vector3 axisMask)
        {
            _name = name;
            _sourceRegion = sourceRegion;
            _targetRegion = targetRegion;
            _stiffness = Mathf.Clamp01(stiffness);
            _axisMask = new Vector3(Mathf.Clamp01(axisMask.x), Mathf.Clamp01(axisMask.y), Mathf.Clamp01(axisMask.z));
        }
    }

    [Serializable]
    public sealed class MeshRegionConstraint
    {
        [SerializeField] private string _region;
        [SerializeField] private float _weight;
        [SerializeField] private float _rigidity;
        [SerializeField] private bool _pinned;

        public string Region => _region;
        public float Weight => _weight;
        public float Rigidity => _rigidity;
        public bool Pinned => _pinned;

        internal MeshRegionConstraint(string region, float weight, float rigidity, bool pinned)
        {
            _region = region;
            _weight = Mathf.Clamp01(weight);
            _rigidity = Mathf.Clamp01(rigidity);
            _pinned = pinned;
        }
    }

    [Serializable]
    public sealed class MeshSelectionState
    {
        [SerializeField] private MeshElementKind _kind = MeshElementKind.Face;
        [SerializeField] private List<int> _indices = new List<int>();
        [SerializeField] private List<MeshNamedRegion> _regions = new List<MeshNamedRegion>();
        [SerializeField] private List<MeshRegionLink> _links = new List<MeshRegionLink>();
        [SerializeField] private List<MeshRegionConstraint> _constraints = new List<MeshRegionConstraint>();
        [SerializeField] private uint _topologyRevision;

        public MeshElementKind Kind => _kind;
        public IReadOnlyList<int> Indices => _indices;
        public IReadOnlyList<MeshNamedRegion> Regions => _regions;
        public IReadOnlyList<MeshRegionLink> Links => _links;
        public IReadOnlyList<MeshRegionConstraint> Constraints => _constraints;
        public uint TopologyRevision => _topologyRevision;

        public void SetSelection(MeshElementKind kind, IEnumerable<int> indices, uint topologyRevision)
        {
            _kind = kind;
            _indices.Clear();
            var unique = new SortedSet<int>();
            if (indices != null) foreach (int index in indices) if (index >= 0) unique.Add(index);
            _indices.AddRange(unique);
            _topologyRevision = topologyRevision;
        }

        public void SaveRegion(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Region name is required.", nameof(name));
            int index = FindRegionIndex(name);
            if (index >= 0) _regions[index] = new MeshNamedRegion(name.Trim(), _kind, _indices);
            else _regions.Add(new MeshNamedRegion(name.Trim(), _kind, _indices));
        }

        public bool SelectRegion(string name, uint topologyRevision)
        {
            int index = FindRegionIndex(name);
            if (index < 0) return false;
            SetSelection(_regions[index].Kind, _regions[index].Indices, topologyRevision);
            return true;
        }

        public void Link(string name, string sourceRegion, string targetRegion, float stiffness, Vector3 axisMask)
        {
            if (FindRegionIndex(sourceRegion) < 0 || FindRegionIndex(targetRegion) < 0)
                throw new InvalidOperationException("REGION_NOT_FOUND: both linked regions must exist.");
            if (string.IsNullOrWhiteSpace(name)) name = sourceRegion + "_to_" + targetRegion;
            for (int i = 0; i < _links.Count; i++)
                if (string.Equals(_links[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _links[i] = new MeshRegionLink(name, sourceRegion, targetRegion, stiffness, axisMask);
                    return;
                }
            _links.Add(new MeshRegionLink(name, sourceRegion, targetRegion, stiffness, axisMask));
        }

        public void SetConstraint(string region, float weight, float rigidity, bool pinned)
        {
            if (FindRegionIndex(region) < 0) throw new InvalidOperationException($"REGION_NOT_FOUND: '{region}'.");
            for (int i = 0; i < _constraints.Count; i++)
                if (string.Equals(_constraints[i].Region, region, StringComparison.OrdinalIgnoreCase))
                {
                    _constraints[i] = new MeshRegionConstraint(region, weight, rigidity, pinned);
                    return;
                }
            _constraints.Add(new MeshRegionConstraint(region, weight, rigidity, pinned));
        }

        public void InvalidateTopology(uint topologyRevision)
        {
            _indices.Clear();
            _regions.Clear();
            _links.Clear();
            _constraints.Clear();
            _topologyRevision = topologyRevision;
        }

        public void RemapTopologyPreservingVertices(uint topologyRevision, int vertexCount)
        {
            if (_kind == MeshElementKind.Vertex) RemoveOutOfRange(_indices, vertexCount);
            else _indices.Clear();
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                if (_regions[i].Kind != MeshElementKind.Vertex) { _regions.RemoveAt(i); continue; }
                var valid = new List<int>();
                for (int j = 0; j < _regions[i].Indices.Count; j++)
                    if (_regions[i].Indices[j] >= 0 && _regions[i].Indices[j] < vertexCount) valid.Add(_regions[i].Indices[j]);
                _regions[i].Replace(valid);
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _regions.Count; i++) names.Add(_regions[i].Name);
            _links.RemoveAll(link => !names.Contains(link.SourceRegion) || !names.Contains(link.TargetRegion));
            _constraints.RemoveAll(constraint => !names.Contains(constraint.Region));
            _topologyRevision = topologyRevision;
        }

        private static void RemoveOutOfRange(List<int> values, int count)
        {
            for (int i = values.Count - 1; i >= 0; i--)
                if (values[i] < 0 || values[i] >= count) values.RemoveAt(i);
        }

        private int FindRegionIndex(string name)
        {
            for (int i = 0; i < _regions.Count; i++)
                if (string.Equals(_regions[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
