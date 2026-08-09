using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [Serializable]
    public sealed class RigTransformPose
    {
        public string Path;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.identity;
        public Vector3 LocalScale = Vector3.one;
    }

    [Serializable]
    public sealed class RigPoseEntry
    {
        public string Id;
        public string Name;
        public string RigType;
        public string HierarchyHash;
        public string CreatedUtc;
        public string Notes;
        public Texture2D Thumbnail;
        public List<RigTransformPose> Transforms = new List<RigTransformPose>();
        public Vector3 HumanBodyPosition;
        public Quaternion HumanBodyRotation = Quaternion.identity;
        public float[] HumanMuscles = Array.Empty<float>();
    }

    [CreateAssetMenu(menuName = "Asset Modeler/Rig Pose Library", fileName = "RigPoseLibrary")]
    public sealed class RigPoseLibrary : ScriptableObject
    {
        [SerializeField] private List<RigPoseEntry> poses = new List<RigPoseEntry>();
        public IReadOnlyList<RigPoseEntry> Poses => poses;
        public void Add(RigPoseEntry pose) { if (pose != null) poses.Add(pose); }
        public bool Remove(string id) => poses.RemoveAll(value => value != null && value.Id == id) > 0;
        public RigPoseEntry Find(string id) => poses.Find(value => value != null && value.Id == id);
        public bool Rename(string id, string name)
        {
            var pose = Find(id);
            if (pose == null || string.IsNullOrWhiteSpace(name)) return false;
            pose.Name = name.Trim();
            return true;
        }

        public RigPoseEntry Duplicate(string id, string name = null)
        {
            var source = Find(id);
            if (source == null) return null;
            var copy = new RigPoseEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? source.Name + " Copy" : name.Trim(),
                RigType = source.RigType,
                HierarchyHash = source.HierarchyHash,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Notes = source.Notes,
                Thumbnail = source.Thumbnail,
                HumanBodyPosition = source.HumanBodyPosition,
                HumanBodyRotation = source.HumanBodyRotation,
                HumanMuscles = source.HumanMuscles != null ? (float[])source.HumanMuscles.Clone() : Array.Empty<float>(),
            };
            for (int i = 0; i < source.Transforms.Count; i++)
            {
                var value = source.Transforms[i];
                copy.Transforms.Add(new RigTransformPose { Path = value.Path, LocalPosition = value.LocalPosition, LocalRotation = value.LocalRotation, LocalScale = value.LocalScale });
            }
            poses.Add(copy);
            return copy;
        }
    }
}
