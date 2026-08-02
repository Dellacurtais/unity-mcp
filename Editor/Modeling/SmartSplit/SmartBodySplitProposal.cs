using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public enum SmartBodySplitPreset
    {
        Core = 0,
        HandsAndFeet = 1,
        Modular = 2,
        Fps = 3,
        Damage = 4,
        Clothing = 5,
    }

    public enum SemanticBodyPart
    {
        Head = 0,
        Torso = 1,
        LeftArm = 2,
        RightArm = 3,
        LeftLeg = 4,
        RightLeg = 5,
        LeftHand = 6,
        RightHand = 7,
        LeftFoot = 8,
        RightFoot = 9,
        Neck = 10,
        Chest = 11,
        AbdomenHip = 12,
        LeftUpperArm = 13,
        LeftLowerArm = 14,
        RightUpperArm = 15,
        RightLowerArm = 16,
        LeftUpperLeg = 17,
        LeftLowerLeg = 18,
        RightUpperLeg = 19,
        RightLowerLeg = 20,
        Unassigned = 21,
    }

    public readonly struct SourceTriangle
    {
        public int SubMesh { get; }
        public int TriangleIndex { get; }
        public int A { get; }
        public int B { get; }
        public int C { get; }
        public float Confidence { get; }

        public SourceTriangle(int subMesh, int triangleIndex, int a, int b, int c, float confidence)
        {
            SubMesh = subMesh;
            TriangleIndex = triangleIndex;
            A = a;
            B = b;
            C = c;
            Confidence = Mathf.Clamp01(confidence);
        }
    }

    public sealed class SmartBodySplitPartProposal
    {
        public SemanticBodyPart Part { get; }
        public List<SourceTriangle> Triangles { get; } = new List<SourceTriangle>();
        public float AverageConfidence { get; internal set; }

        public string SlotId => SmartBodySplitPartNames.SlotId(Part);
        public string Label => SmartBodySplitPartNames.Label(Part);

        public SmartBodySplitPartProposal(SemanticBodyPart part)
        {
            Part = part;
        }
    }

    public sealed class SmartBodySplitProposal
    {
        public GameObject Target { get; internal set; }
        public Renderer SourceRenderer { get; internal set; }
        public Mesh SourceMesh { get; internal set; }
        public string SourceHash { get; internal set; }
        public string PreviewHash { get; internal set; }
        public string RigType { get; internal set; }
        public SmartBodySplitPreset Preset { get; internal set; }
        public bool IsRigged { get; internal set; }
        public float AverageConfidence { get; internal set; }
        public int SourceTriangleCount { get; internal set; }
        public List<SmartBodySplitPartProposal> Parts { get; } = new List<SmartBodySplitPartProposal>();
        public List<string> Warnings { get; } = new List<string>();

        public SmartBodySplitPartProposal Find(SemanticBodyPart part)
            => Parts.FirstOrDefault(candidate => candidate.Part == part);

        public bool TryReassignTriangle(
            int subMesh,
            int triangleIndex,
            SemanticBodyPart targetPart,
            out string error)
        {
            error = null;
            if (!SmartBodySplitPresets.Parts(Preset).Contains(targetPart))
            {
                error = $"Part '{targetPart}' is not available in preset '{SmartBodySplitPresets.Id(Preset)}'.";
                return false;
            }
            SmartBodySplitPartProposal sourcePart = null;
            SourceTriangle triangle = default;
            int sourceIndex = -1;
            for (int partIndex = 0; partIndex < Parts.Count && sourcePart == null; partIndex++)
                for (int index = 0; index < Parts[partIndex].Triangles.Count; index++)
                {
                    var candidate = Parts[partIndex].Triangles[index];
                    if (candidate.SubMesh != subMesh || candidate.TriangleIndex != triangleIndex) continue;
                    sourcePart = Parts[partIndex];
                    sourceIndex = index;
                    triangle = candidate;
                    break;
                }
            if (sourcePart == null)
            {
                error = $"Triangle {subMesh}:{triangleIndex} is not part of this proposal.";
                return false;
            }
            if (sourcePart.Part == targetPart) return true;
            var destination = Find(targetPart);
            if (destination == null)
            {
                destination = new SmartBodySplitPartProposal(targetPart);
                Parts.Add(destination);
            }
            sourcePart.Triangles.RemoveAt(sourceIndex);
            destination.Triangles.Add(new SourceTriangle(
                triangle.SubMesh,
                triangle.TriangleIndex,
                triangle.A,
                triangle.B,
                triangle.C,
                1f));
            Parts.RemoveAll(part => part.Triangles.Count == 0);
            FinalizeProposal();
            return true;
        }

        internal void FinalizeProposal()
        {
            SourceTriangleCount = 0;
            double confidence = 0d;
            for (int i = 0; i < Parts.Count; i++)
            {
                var part = Parts[i];
                double partConfidence = 0d;
                for (int triangleIndex = 0; triangleIndex < part.Triangles.Count; triangleIndex++)
                    partConfidence += part.Triangles[triangleIndex].Confidence;
                part.AverageConfidence = part.Triangles.Count > 0
                    ? (float)(partConfidence / part.Triangles.Count)
                    : 0f;
                SourceTriangleCount += part.Triangles.Count;
                confidence += partConfidence;
            }
            AverageConfidence = SourceTriangleCount > 0 ? (float)(confidence / SourceTriangleCount) : 0f;
            PreviewHash = ComputePreviewHash();
        }

        private string ComputePreviewHash()
        {
            var canonical = new StringBuilder(SourceTriangleCount * 20);
            canonical.Append(SourceHash).Append('|').Append(RigType).Append('|').Append((int)Preset).Append('|');
            foreach (var part in Parts.OrderBy(candidate => candidate.Part))
            {
                canonical.Append((int)part.Part).Append(':');
                foreach (var triangle in part.Triangles.OrderBy(value => value.SubMesh).ThenBy(value => value.TriangleIndex))
                    canonical.Append(triangle.SubMesh).Append(',').Append(triangle.TriangleIndex).Append(';');
            }
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    public static class SmartBodySplitPartNames
    {
        public static bool TryParse(string value, out SemanticBodyPart part)
        {
            for (int index = 0; index < (int)SemanticBodyPart.Unassigned; index++)
            {
                var candidate = (SemanticBodyPart)index;
                if (string.Equals(SlotId(candidate), value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    part = candidate;
                    return true;
                }
            }
            part = SemanticBodyPart.Unassigned;
            return false;
        }

        public static string SlotId(SemanticBodyPart part)
        {
            switch (part)
            {
                case SemanticBodyPart.Head: return "head";
                case SemanticBodyPart.Torso: return "torso";
                case SemanticBodyPart.LeftArm: return "arm.left";
                case SemanticBodyPart.RightArm: return "arm.right";
                case SemanticBodyPart.LeftLeg: return "leg.left";
                case SemanticBodyPart.RightLeg: return "leg.right";
                case SemanticBodyPart.LeftHand: return "hand.left";
                case SemanticBodyPart.RightHand: return "hand.right";
                case SemanticBodyPart.LeftFoot: return "foot.left";
                case SemanticBodyPart.RightFoot: return "foot.right";
                case SemanticBodyPart.Neck: return "neck";
                case SemanticBodyPart.Chest: return "chest";
                case SemanticBodyPart.AbdomenHip: return "abdomen_hip";
                case SemanticBodyPart.LeftUpperArm: return "upper_arm.left";
                case SemanticBodyPart.LeftLowerArm: return "lower_arm.left";
                case SemanticBodyPart.RightUpperArm: return "upper_arm.right";
                case SemanticBodyPart.RightLowerArm: return "lower_arm.right";
                case SemanticBodyPart.LeftUpperLeg: return "upper_leg.left";
                case SemanticBodyPart.LeftLowerLeg: return "lower_leg.left";
                case SemanticBodyPart.RightUpperLeg: return "upper_leg.right";
                case SemanticBodyPart.RightLowerLeg: return "lower_leg.right";
                default: return "unassigned";
            }
        }

        public static string Label(SemanticBodyPart part)
        {
            switch (part)
            {
                case SemanticBodyPart.Head: return "Cabeça";
                case SemanticBodyPart.Torso: return "Tronco/Peitoral";
                case SemanticBodyPart.LeftArm: return "Braço esquerdo";
                case SemanticBodyPart.RightArm: return "Braço direito";
                case SemanticBodyPart.LeftLeg: return "Perna esquerda";
                case SemanticBodyPart.RightLeg: return "Perna direita";
                case SemanticBodyPart.LeftHand: return "Mão esquerda";
                case SemanticBodyPart.RightHand: return "Mão direita";
                case SemanticBodyPart.LeftFoot: return "Pé esquerdo";
                case SemanticBodyPart.RightFoot: return "Pé direito";
                case SemanticBodyPart.Neck: return "Pescoço";
                case SemanticBodyPart.Chest: return "Peitoral";
                case SemanticBodyPart.AbdomenHip: return "Abdômen/Quadril";
                case SemanticBodyPart.LeftUpperArm: return "Braço superior esquerdo";
                case SemanticBodyPart.LeftLowerArm: return "Antebraço esquerdo";
                case SemanticBodyPart.RightUpperArm: return "Braço superior direito";
                case SemanticBodyPart.RightLowerArm: return "Antebraço direito";
                case SemanticBodyPart.LeftUpperLeg: return "Coxa esquerda";
                case SemanticBodyPart.LeftLowerLeg: return "Canela esquerda";
                case SemanticBodyPart.RightUpperLeg: return "Coxa direita";
                case SemanticBodyPart.RightLowerLeg: return "Canela direita";
                default: return "Não atribuído";
            }
        }
    }

    public static class SmartBodySplitPresets
    {
        private static readonly IReadOnlyList<SemanticBodyPart> CoreParts = Array.AsReadOnly(new[]
        {
            SemanticBodyPart.Head,
            SemanticBodyPart.Torso,
            SemanticBodyPart.LeftArm,
            SemanticBodyPart.RightArm,
            SemanticBodyPart.LeftLeg,
            SemanticBodyPart.RightLeg,
        });

        private static readonly IReadOnlyList<SemanticBodyPart> HandsAndFeetParts = Array.AsReadOnly(new[]
        {
            SemanticBodyPart.Head,
            SemanticBodyPart.Torso,
            SemanticBodyPart.LeftArm,
            SemanticBodyPart.RightArm,
            SemanticBodyPart.LeftLeg,
            SemanticBodyPart.RightLeg,
            SemanticBodyPart.LeftHand,
            SemanticBodyPart.RightHand,
            SemanticBodyPart.LeftFoot,
            SemanticBodyPart.RightFoot,
        });

        private static readonly IReadOnlyList<SemanticBodyPart> ModularParts = Array.AsReadOnly(new[]
        {
            SemanticBodyPart.Head,
            SemanticBodyPart.Neck,
            SemanticBodyPart.Chest,
            SemanticBodyPart.AbdomenHip,
            SemanticBodyPart.LeftUpperArm,
            SemanticBodyPart.LeftLowerArm,
            SemanticBodyPart.RightUpperArm,
            SemanticBodyPart.RightLowerArm,
            SemanticBodyPart.LeftHand,
            SemanticBodyPart.RightHand,
            SemanticBodyPart.LeftUpperLeg,
            SemanticBodyPart.LeftLowerLeg,
            SemanticBodyPart.RightUpperLeg,
            SemanticBodyPart.RightLowerLeg,
            SemanticBodyPart.LeftFoot,
            SemanticBodyPart.RightFoot,
        });

        private static readonly IReadOnlyList<SemanticBodyPart> FpsParts = Array.AsReadOnly(new[]
        {
            SemanticBodyPart.Head,
            SemanticBodyPart.Chest,
            SemanticBodyPart.AbdomenHip,
            SemanticBodyPart.LeftUpperArm,
            SemanticBodyPart.LeftLowerArm,
            SemanticBodyPart.RightUpperArm,
            SemanticBodyPart.RightLowerArm,
            SemanticBodyPart.LeftHand,
            SemanticBodyPart.RightHand,
            SemanticBodyPart.LeftLeg,
            SemanticBodyPart.RightLeg,
        });

        private static readonly IReadOnlyList<SemanticBodyPart> ClothingParts = Array.AsReadOnly(new[]
        {
            SemanticBodyPart.Head,
            SemanticBodyPart.Chest,
            SemanticBodyPart.AbdomenHip,
            SemanticBodyPart.LeftArm,
            SemanticBodyPart.RightArm,
            SemanticBodyPart.LeftHand,
            SemanticBodyPart.RightHand,
            SemanticBodyPart.LeftLeg,
            SemanticBodyPart.RightLeg,
            SemanticBodyPart.LeftFoot,
            SemanticBodyPart.RightFoot,
        });

        public static IReadOnlyList<SemanticBodyPart> Parts(SmartBodySplitPreset preset)
        {
            switch (preset)
            {
                case SmartBodySplitPreset.HandsAndFeet: return HandsAndFeetParts;
                case SmartBodySplitPreset.Modular:
                case SmartBodySplitPreset.Damage: return ModularParts;
                case SmartBodySplitPreset.Fps: return FpsParts;
                case SmartBodySplitPreset.Clothing: return ClothingParts;
                default: return CoreParts;
            }
        }

        public static string Id(SmartBodySplitPreset preset)
        {
            switch (preset)
            {
                case SmartBodySplitPreset.HandsAndFeet: return "hands_feet";
                case SmartBodySplitPreset.Modular: return "modular";
                case SmartBodySplitPreset.Fps: return "fps";
                case SmartBodySplitPreset.Damage: return "damage";
                case SmartBodySplitPreset.Clothing: return "clothing";
                default: return "core";
            }
        }

        public static string Label(SmartBodySplitPreset preset)
        {
            switch (preset)
            {
                case SmartBodySplitPreset.HandsAndFeet: return "Corpo + mãos e pés (10 partes)";
                case SmartBodySplitPreset.Modular: return "Corpo modular completo (16 partes)";
                case SmartBodySplitPreset.Fps: return "FPS — braços detalhados (11 partes)";
                case SmartBodySplitPreset.Damage: return "Dano/desmembramento (16 partes)";
                case SmartBodySplitPreset.Clothing: return "Roupas e acessórios (11 regiões)";
                default: return "Corpo principal (6 partes)";
            }
        }

        public static SmartBodySplitPreset Parse(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "hands_feet": return SmartBodySplitPreset.HandsAndFeet;
                case "modular": return SmartBodySplitPreset.Modular;
                case "fps": return SmartBodySplitPreset.Fps;
                case "damage": return SmartBodySplitPreset.Damage;
                case "clothing": return SmartBodySplitPreset.Clothing;
                default: return SmartBodySplitPreset.Core;
            }
        }

        public static bool HasSeparateHands(SmartBodySplitPreset preset)
            => preset != SmartBodySplitPreset.Core;

        public static bool HasSeparateFeet(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.HandsAndFeet
                || preset == SmartBodySplitPreset.Modular
                || preset == SmartBodySplitPreset.Damage
                || preset == SmartBodySplitPreset.Clothing;

        public static bool HasDetailedArms(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.Modular
                || preset == SmartBodySplitPreset.Damage
                || preset == SmartBodySplitPreset.Fps;

        public static bool HasDetailedLegs(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.Modular || preset == SmartBodySplitPreset.Damage;

        public static bool HasDetailedTorso(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.Modular
                || preset == SmartBodySplitPreset.Damage
                || preset == SmartBodySplitPreset.Fps
                || preset == SmartBodySplitPreset.Clothing;
    }
}
