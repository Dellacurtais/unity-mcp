using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [CreateAssetMenu(menuName = "Asset Modeler/Rig Bind Pose Snapshot", fileName = "RigBindPoseSnapshot")]
    public sealed class RigBindPoseSnapshot : ScriptableObject
    {
        public string CharacterStateHash;
        public string HierarchyHash;
        public string RendererPath;
        public string RootBonePath;
        public List<string> BonePaths = new List<string>();
        public List<Matrix4x4> BindPoses = new List<Matrix4x4>();
        public string CreatedUtc;
    }
}
