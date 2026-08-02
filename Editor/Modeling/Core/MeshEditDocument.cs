using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public enum MeshEditDocumentState
    {
        Active,
        SourceDrifted,
        Invalid,
        Committed,
        Discarded,
    }

    [Serializable]
    public sealed class MeshModifierRecord
    {
        [SerializeField] private string _operationId;
        [SerializeField] private string _displayName;
        [SerializeField] private string _parametersJson;
        [SerializeField] private string _inputHash;
        [SerializeField] private string _resultHash;
        [SerializeField] private MeshRevision _revisionBefore;
        [SerializeField] private MeshRevision _revisionAfter;
        [SerializeField] private bool _enabled = true;
        [SerializeField] private long _createdUtcTicks;

        public string OperationId => _operationId;
        public string DisplayName => _displayName;
        public string ParametersJson => _parametersJson;
        public string InputHash => _inputHash;
        public string ResultHash => _resultHash;
        public MeshRevision RevisionBefore => _revisionBefore;
        public MeshRevision RevisionAfter => _revisionAfter;
        public bool Enabled => _enabled;
        public DateTime CreatedUtc => new DateTime(_createdUtcTicks, DateTimeKind.Utc);

        public MeshModifierRecord(
            string operationId,
            string displayName,
            string parametersJson,
            string inputHash,
            string resultHash,
            MeshRevision revisionBefore,
            MeshRevision revisionAfter)
        {
            _operationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
            _displayName = displayName ?? operationId;
            _parametersJson = parametersJson ?? "{}";
            _inputHash = inputHash ?? string.Empty;
            _resultHash = resultHash ?? string.Empty;
            _revisionBefore = revisionBefore;
            _revisionAfter = revisionAfter;
            _enabled = true;
            _createdUtcTicks = DateTime.UtcNow.Ticks;
        }

        internal void SetEnabled(bool enabled) => _enabled = enabled;
    }

    public sealed class MeshEditDocument : ScriptableObject
    {
        [SerializeField] private string _documentId;
        [SerializeField] private string _sessionId;
        [SerializeField] private string _sourceAssetGuid;
        [SerializeField] private long _sourceLocalFileId;
        [SerializeField] private string _sourceHash;
        [SerializeField] private string _sourceGlobalObjectId;
        [SerializeField] private Mesh _sourceMesh;
        [SerializeField] private Mesh _workingMesh;
        [SerializeField] private string _workingMeshGuid;
        [SerializeField] private MeshRevision _revision;
        [SerializeField] private MeshChannelManifest _channels;
        [SerializeField] private MeshSelectionState _selection = new MeshSelectionState();
        [SerializeField] private MeshEditDocumentState _state;
        [SerializeField] private List<MeshModifierRecord> _modifierStack = new List<MeshModifierRecord>();
        [SerializeField] private long _createdUtcTicks;
        [SerializeField] private long _lastModifiedUtcTicks;

        public string DocumentId => _documentId;
        public string SessionId => _sessionId;
        public string SourceAssetGuid => _sourceAssetGuid;
        public long SourceLocalFileId => _sourceLocalFileId;
        public string SourceHash => _sourceHash;
        public string SourceGlobalObjectId => _sourceGlobalObjectId;
        public Mesh SourceMesh => _sourceMesh;
        public Mesh WorkingMesh => _workingMesh;
        public string WorkingMeshGuid => _workingMeshGuid;
        public MeshRevision Revision => _revision;
        public MeshChannelManifest Channels => _channels;
        public MeshSelectionState Selection => _selection ?? (_selection = new MeshSelectionState());
        public MeshEditDocumentState State => _state;
        public IReadOnlyList<MeshModifierRecord> ModifierStack => _modifierStack;
        public DateTime CreatedUtc => new DateTime(_createdUtcTicks, DateTimeKind.Utc);
        public DateTime LastModifiedUtc => new DateTime(_lastModifiedUtcTicks, DateTimeKind.Utc);

        public void Initialize(GameObject sourceObject, Mesh sourceMesh, Mesh workingMesh)
        {
            if (sourceMesh == null) throw new ArgumentNullException(nameof(sourceMesh));
            if (workingMesh == null) throw new ArgumentNullException(nameof(workingMesh));
            _documentId = Guid.NewGuid().ToString("N");
            _sessionId = Guid.NewGuid().ToString("N");
            _sourceAssetGuid = string.Empty;
            _sourceLocalFileId = 0;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceMesh, out _sourceAssetGuid, out _sourceLocalFileId);
            _sourceHash = MeshFingerprint.Compute(sourceMesh);
            _sourceMesh = sourceMesh;
            _sourceGlobalObjectId = sourceObject != null
                ? GlobalObjectId.GetGlobalObjectIdSlow(sourceObject).ToString()
                : string.Empty;
            _workingMesh = workingMesh;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(workingMesh, out _workingMeshGuid, out long _);
            _revision = new MeshRevision(0, 0);
            _channels = MeshChannelManifest.Capture(workingMesh);
            _selection = new MeshSelectionState();
            _state = MeshEditDocumentState.Active;
            _modifierStack.Clear();
            _createdUtcTicks = DateTime.UtcNow.Ticks;
            _lastModifiedUtcTicks = _createdUtcTicks;
        }

        public bool Matches(MeshRevision expected) => _revision.Equals(expected);

        public void AdvanceTopology(bool preserveOriginalVertexSelection = false)
        {
            _revision = _revision.NextTopology();
            if (preserveOriginalVertexSelection)
                Selection.RemapTopologyPreservingVertices(_revision.Topology, _workingMesh != null ? _workingMesh.vertexCount : 0);
            else Selection.InvalidateTopology(_revision.Topology);
            _channels = _workingMesh != null ? MeshChannelManifest.Capture(_workingMesh) : null;
            Touch();
        }

        public void AdvanceGeometry()
        {
            _revision = _revision.NextGeometry();
            _channels = _workingMesh != null ? MeshChannelManifest.Capture(_workingMesh) : null;
            Touch();
        }

        public void AddModifier(MeshModifierRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            _modifierStack.Add(record);
            Touch();
        }

        public void RemoveModifierAt(int index)
        {
            if (index < 0 || index >= _modifierStack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            _modifierStack.RemoveAt(index);
            Touch();
        }

        internal void InsertModifierAt(int index, MeshModifierRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (index < 0 || index > _modifierStack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            _modifierStack.Insert(index, record);
            Touch();
        }

        public void MoveModifier(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= _modifierStack.Count) throw new ArgumentOutOfRangeException(nameof(fromIndex));
            if (toIndex < 0 || toIndex >= _modifierStack.Count) throw new ArgumentOutOfRangeException(nameof(toIndex));
            if (fromIndex == toIndex) return;
            var record = _modifierStack[fromIndex];
            _modifierStack.RemoveAt(fromIndex);
            _modifierStack.Insert(toIndex, record);
            Touch();
        }

        public void SetModifierEnabled(int index, bool enabled)
        {
            if (index < 0 || index >= _modifierStack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            _modifierStack[index].SetEnabled(enabled);
            Touch();
        }

        public void ClearModifiers()
        {
            _modifierStack.Clear();
            Touch();
        }

        public void SetState(MeshEditDocumentState state)
        {
            _state = state;
            Touch();
        }

        public bool HasSourceDrift()
        {
            if (_sourceMesh == null) return true;
            return !string.Equals(_sourceHash, MeshFingerprint.Compute(_sourceMesh), StringComparison.Ordinal);
        }

        public void RebaseToSource()
        {
            if (_sourceMesh == null) throw new InvalidOperationException("SOURCE_MISSING: source mesh is no longer available.");
            _sourceHash = MeshFingerprint.Compute(_sourceMesh);
            _revision = _revision.NextTopology();
            Selection.InvalidateTopology(_revision.Topology);
            _state = MeshEditDocumentState.Active;
            _modifierStack.Clear();
            _channels = _workingMesh != null ? MeshChannelManifest.Capture(_workingMesh) : null;
            Touch();
        }

        private void Touch()
        {
            _lastModifiedUtcTicks = DateTime.UtcNow.Ticks;
            EditorUtility.SetDirty(this);
        }
    }
}
