using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Deform;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Commands
{
    public static class ModelingOperationCatalog
    {
        private static bool _registered;

        public static void RegisterAll()
        {
            if (_registered) return;
            _registered = true;
            MeshOperationRegistry.Register("mesh.transform_vertices", TransformVerticesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.mirror", MirrorMeshOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.cleanup", CleanupMeshOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.weld", WeldVerticesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.subdivide", SubdivideTrianglesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.extrude_faces", ExtrudeFacesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.extrude_edges", ExtrudeBoundaryEdgesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.bevel_faces", BevelFacesOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.bridge_loops", BridgeLoopsOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.fill_hole", FillHoleOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.adaptive_refine", AdaptiveRefineOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.deform", DeformMeshOperation.Deserialize);
            MeshOperationRegistry.Register("mesh.surface_fit", SmartSurfaceFitOperation.Deserialize);
        }
    }
}
