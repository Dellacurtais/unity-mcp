using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [Serializable]
    public sealed class RigRetargetBoneMap
    {
        public string SourcePath;
        public string DestinationPath;
        public string HumanBone;
        public Quaternion RotationOffset = Quaternion.identity;
        [Range(0f, 1f)] public float Confidence;
        public bool Enabled = true;
        public bool Reviewed;
        public string Warning;
    }

    [CreateAssetMenu(menuName = "Asset Modeler/Rig Retarget Profile", fileName = "RigRetargetProfile")]
    public sealed class RigRetargetProfile : ScriptableObject
    {
        public string SourceHierarchyHash;
        public string DestinationHierarchyHash;
        public string SourceStateHash;
        public string DestinationStateHash;
        public string SourceRigType;
        public string DestinationRigType;
        public float RootScale = 1f;
        public bool TransferRootMotion = true;
        public List<RigRetargetBoneMap> Bones = new List<RigRetargetBoneMap>();
    }
}
