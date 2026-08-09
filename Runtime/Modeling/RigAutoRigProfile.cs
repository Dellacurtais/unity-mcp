using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [Serializable]
    public sealed class RigAutoRigLandmark
    {
        public string Id;
        public string ParentId;
        public Vector3 LocalPosition;
        [Range(0f, 1f)] public float Confidence;
        public string Source;
    }

    [CreateAssetMenu(menuName = "Asset Modeler/Auto Rig Profile", fileName = "RigAutoRigProfile")]
    public sealed class RigAutoRigProfile : ScriptableObject
    {
        public string ContractVersion = "1.0";
        public string MeshHash;
        public string ProposalHash;
        public float AverageConfidence;
        public string AvatarStatus;
        public Avatar AvatarAsset;
        public AnimationClip ValidationClip;
        public List<RigAutoRigLandmark> Landmarks = new List<RigAutoRigLandmark>();
    }
}
