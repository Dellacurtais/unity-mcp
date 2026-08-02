using System.Diagnostics;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using NUnit.Framework;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class ModelingBenchmarkTests
    {
        [Test]
        [Explicit("Manual Phase 0 performance baseline; results depend on editor hardware.")]
        [Category("Performance")]
        public void StaticGrid100kTriangles_CaptureAndAnalyzeBaseline()
        {
            var mesh = ModelingTestMeshFactory.CreateGrid(250, 200);
            var gameObject = new GameObject("Grid100k");
            try
            {
                gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                gameObject.AddComponent<MeshRenderer>();

                var stopwatch = Stopwatch.StartNew();
                var snapshot = MeshChannelSnapshot.Capture(mesh);
                stopwatch.Stop();
                long captureMs = stopwatch.ElapsedMilliseconds;

                stopwatch.Restart();
                var proposal = SmartBodySplitAnalyzer.Analyze(gameObject);
                stopwatch.Stop();

                TestContext.WriteLine(
                    $"vertices={mesh.vertexCount}; triangles={proposal.SourceTriangleCount}; " +
                    $"captureMs={captureMs}; analyzeMs={stopwatch.ElapsedMilliseconds}; " +
                    $"channels={snapshot.Manifest.VertexAttributes.Count}");
                Assert.That(proposal.SourceTriangleCount, Is.EqualTo(100000));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
