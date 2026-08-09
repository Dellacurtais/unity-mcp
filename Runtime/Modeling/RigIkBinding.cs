using UnityEngine;
using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Modeling
{
    public enum RigIkSolverType { TwoBone, Aim, Fabrik }
    public enum RigIkPlacementPreset { None, HandReachForward, HandAtChest, FootPlantGround }

    [DisallowMultipleComponent]
    public sealed class RigIkBinding : MonoBehaviour
    {
        public RigIkSolverType Solver = RigIkSolverType.TwoBone;
        public Transform CharacterRoot;
        public Transform Root;
        public Transform Mid;
        public Transform Tip;
        public Transform Target;
        public Transform Hint;
        public List<Transform> Chain = new List<Transform>();
        public List<Quaternion> RestLocalRotations = new List<Quaternion>();
        public RigIkPlacementPreset PlacementPreset;
        [Range(1, 32)] public int Iterations = 8;
        [Range(0f, 1f)] public float Weight = 1f;
    }
}
