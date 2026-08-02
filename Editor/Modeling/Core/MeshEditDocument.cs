using System;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public sealed class MeshEditDocument : ScriptableObject
    {
        [SerializeField] private string _documentId;
        [SerializeField] private string _sourceAssetGuid;
        [SerializeField] private long _sourceLocalFileId;
        [SerializeField] private string _sourceHash;
        [SerializeField] private string _sourceGlobalObjectId;
        [SerializeField] private Mesh _workingMesh;
        [SerializeField] private MeshRevision _revision;
        [SerializeField] private MeshChannelManifest _channels;

        public string DocumentId => _documentId;
        public string SourceAssetGuid => _sourceAssetGuid;
        public long SourceLocalFileId => _sourceLocalFileId;
        public string SourceHash => _sourceHash;
        public string SourceGlobalObjectId => _sourceGlobalObjectId;
        public Mesh WorkingMesh => _workingMesh;
        public MeshRevision Revision => _revision;
        public MeshChannelManifest Channels => _channels;

        public void Initialize(GameObject sourceObject, Mesh sourceMesh, Mesh workingMesh)
        {
            if (sourceMesh == null) throw new ArgumentNullException(nameof(sourceMesh));
            if (workingMesh == null) throw new ArgumentNullException(nameof(workingMesh));
            _documentId = Guid.NewGuid().ToString("N");
            _sourceAssetGuid = string.Empty;
            _sourceLocalFileId = 0;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceMesh, out _sourceAssetGuid, out _sourceLocalFileId);
            _sourceHash = MeshFingerprint.Compute(sourceMesh);
            _sourceGlobalObjectId = sourceObject != null
                ? GlobalObjectId.GetGlobalObjectIdSlow(sourceObject).ToString()
                : string.Empty;
            _workingMesh = workingMesh;
            _revision = new MeshRevision(0, 0);
            _channels = MeshChannelManifest.Capture(workingMesh);
        }

        public bool Matches(MeshRevision expected) => _revision.Equals(expected);

        public void AdvanceTopology()
        {
            _revision = _revision.NextTopology();
            _channels = _workingMesh != null ? MeshChannelManifest.Capture(_workingMesh) : null;
        }

        public void AdvanceGeometry()
        {
            _revision = _revision.NextGeometry();
            _channels = _workingMesh != null ? MeshChannelManifest.Capture(_workingMesh) : null;
        }
    }
}
