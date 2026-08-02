using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Modeling
{
    [Serializable]
    public sealed class ModularCharacterSlot
    {
        [SerializeField] private string _slotId;
        [SerializeField] private SkinnedMeshRenderer _renderer;

        public string SlotId => _slotId;
        public SkinnedMeshRenderer Renderer => _renderer;

        public void Configure(string slotId, SkinnedMeshRenderer renderer)
        {
            _slotId = slotId ?? string.Empty;
            _renderer = renderer;
        }
    }

    public sealed class ModularCharacterAssembler : MonoBehaviour
    {
        [SerializeField] private BodyPartSet _bodyPartSet;
        [SerializeField] private Transform _rootBone;
        [SerializeField] private Transform[] _bones = Array.Empty<Transform>();
        [SerializeField] private List<ModularCharacterSlot> _slots = new List<ModularCharacterSlot>();

        public BodyPartSet BodyPartSet => _bodyPartSet;
        public Transform RootBone => _rootBone;
        public IReadOnlyList<Transform> Bones => _bones;
        public IReadOnlyList<ModularCharacterSlot> Slots => _slots;

        public void Configure(
            BodyPartSet bodyPartSet,
            Transform rootBone,
            Transform[] bones,
            IEnumerable<ModularCharacterSlot> slots)
        {
            _bodyPartSet = bodyPartSet;
            _rootBone = rootBone;
            _bones = bones != null ? (Transform[])bones.Clone() : Array.Empty<Transform>();
            _slots = slots != null ? new List<ModularCharacterSlot>(slots) : new List<ModularCharacterSlot>();
        }

        public bool TryApplyPart(string slotId, BodyPartDefinition part, out string error)
        {
            error = null;
            if (part == null || part.Mesh == null)
            {
                error = "Part or part mesh is null.";
                return false;
            }

            ModularCharacterSlot slot = null;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (string.Equals(_slots[i]?.SlotId, slotId, StringComparison.Ordinal))
                {
                    slot = _slots[i];
                    break;
                }
            }
            if (slot?.Renderer == null)
            {
                error = $"Slot '{slotId}' has no renderer.";
                return false;
            }

            var bindPoseCount = part.Mesh.bindposes != null ? part.Mesh.bindposes.Length : 0;
            if (bindPoseCount > 0 && bindPoseCount != _bones.Length)
            {
                error = $"Part bind pose count {bindPoseCount} does not match assembler bone count {_bones.Length}.";
                return false;
            }

            slot.Renderer.sharedMesh = part.Mesh;
            slot.Renderer.rootBone = _rootBone;
            slot.Renderer.bones = _bones;
            if (part.Materials.Count > 0)
            {
                var materials = new Material[part.Materials.Count];
                for (int i = 0; i < materials.Length; i++) materials[i] = part.Materials[i];
                slot.Renderer.sharedMaterials = materials;
            }
            return true;
        }
    }
}
