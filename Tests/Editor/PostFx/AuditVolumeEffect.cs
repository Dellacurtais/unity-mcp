using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tests
{
    [Serializable]
    public sealed class AuditVolumeEffect : VolumeComponent
    {
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);
        public Vector4Parameter vector = new Vector4Parameter(Vector4.zero);
    }
}
