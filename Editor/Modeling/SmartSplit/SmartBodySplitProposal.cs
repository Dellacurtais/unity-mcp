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
        Unassigned = 10,
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

        public static IReadOnlyList<SemanticBodyPart> Parts(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.HandsAndFeet ? HandsAndFeetParts : CoreParts;

        public static string Id(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.HandsAndFeet ? "hands_feet" : "core";

        public static string Label(SmartBodySplitPreset preset)
            => preset == SmartBodySplitPreset.HandsAndFeet
                ? "Corpo + mãos e pés (10 partes)"
                : "Corpo principal (6 partes)";

        public static SmartBodySplitPreset Parse(string value)
            => string.Equals(value, "hands_feet", StringComparison.OrdinalIgnoreCase)
                ? SmartBodySplitPreset.HandsAndFeet
                : SmartBodySplitPreset.Core;
    }
}
