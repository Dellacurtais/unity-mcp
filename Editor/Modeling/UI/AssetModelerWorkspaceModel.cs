using System;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    internal enum AssetModelerWorkspace
    {
        Home = 0,
        Session = 1,
        Objects = 2,
        Topology = 3,
        Deform = 4,
        SurfaceFit = 5,
        Cleanup = 6,
        Size = 7,
        Save = 8,
    }

    internal enum AssetModelerObjectTool
    {
        Transform = 0, // Legacy serialized value; no longer exposed as an object tool.
        Mirror = 1,
        Array = 2,
        Merge = 3,
        Export = 4,
    }

    internal enum AssetModelerTopologyTool
    {
        Cleanup,
        Weld,
        Subdivide,
        ExtrudeFaces,
        Bevel,
        AdaptiveRefine,
        ExtrudeEdges,
        BridgeLoops,
        FillHole,
    }

    [Serializable]
    internal sealed class AssetModelerNavigationState
    {
        [UnityEngine.SerializeField]
        private AssetModelerWorkspace _workspace = AssetModelerWorkspace.Home;

        public AssetModelerWorkspace Workspace => _workspace;
        public bool IsHome => _workspace == AssetModelerWorkspace.Home;

        public void Open(AssetModelerWorkspace workspace) => _workspace = workspace;
        public void Back() => _workspace = AssetModelerWorkspace.Home;
    }

    internal static class AssetModelerWorkspaceCatalog
    {
        public static readonly string[] ObjectToolLabels =
        {
            "Espelhar", "Repetir", "Combinar", "Exportar",
        };

        public static readonly AssetModelerObjectTool[] ObjectTools =
        {
            AssetModelerObjectTool.Mirror, AssetModelerObjectTool.Array,
            AssetModelerObjectTool.Merge, AssetModelerObjectTool.Export,
        };

        public static AssetModelerObjectTool VisibleObjectTool(AssetModelerObjectTool tool)
            => Array.IndexOf(ObjectTools, tool) >= 0 ? tool : AssetModelerObjectTool.Mirror;

        public static readonly string[] TopologyToolLabels =
        {
            "Limpar", "Soldar", "Subdividir", "Extrudar", "Bevel",
            "Refinar", "Extrudar aresta", "Bridge", "Fechar buraco",
        };

        public static string Label(AssetModelerWorkspace workspace)
        {
            switch (workspace)
            {
                case AssetModelerWorkspace.Session: return "Detalhes da edição";
                case AssetModelerWorkspace.Objects: return "Construir e combinar";
                case AssetModelerWorkspace.Cleanup: return "Limpar geometria";
                case AssetModelerWorkspace.Size: return "Ajustar tamanho";
                case AssetModelerWorkspace.Save: return "Salvar cópia";
                case AssetModelerWorkspace.Topology: return "Topologia";
                case AssetModelerWorkspace.Deform: return "Deformação";
                case AssetModelerWorkspace.SurfaceFit: return "Ajustar roupa ao corpo";
                default: return "Início";
            }
        }

        public static string Description(AssetModelerWorkspace workspace)
        {
            switch (workspace)
            {
                case AssetModelerWorkspace.Session: return "Crie, valide e finalize uma cópia editável sem alterar o FBX importado.";
                case AssetModelerWorkspace.Objects: return "Espelhe malhas, crie repetições, combine ou exporte.";
                case AssetModelerWorkspace.Cleanup: return "Revise faces sem área, vértices soltos e fragmentos antes de remover.";
                case AssetModelerWorkspace.Size: return "Ajuste o modelo inteiro e gere uma cópia com escala (1, 1, 1).";
                case AssetModelerWorkspace.Save: return "Salve a hierarquia da cópia como prefab, preservando materiais e rig.";
                case AssetModelerWorkspace.Topology: return "Selecione faces no Scene View e execute uma operação de malha por vez.";
                case AssetModelerWorkspace.Deform: return "Modele com soft selection, curvas, Laplacian e ARAP com preview.";
                case AssetModelerWorkspace.SurfaceFit: return "Ajuste roupa ou acessórios sobre outra superfície mantendo distância.";
                default: return "Escolha uma tarefa. Só os controles relevantes serão exibidos.";
            }
        }

        public static bool TopologyUsesFaces(AssetModelerTopologyTool tool)
            => tool == AssetModelerTopologyTool.Subdivide
                || tool == AssetModelerTopologyTool.ExtrudeFaces
                || tool == AssetModelerTopologyTool.Bevel;

        public static bool SupportsMeshPreview(
            AssetModelerWorkspace workspace,
            AssetModelerObjectTool objectTool,
            AssetModelerTopologyTool topologyTool)
        {
            switch (workspace)
            {
                case AssetModelerWorkspace.Objects:
                    return objectTool == AssetModelerObjectTool.Mirror;
                case AssetModelerWorkspace.Cleanup:
                case AssetModelerWorkspace.Topology:
                case AssetModelerWorkspace.Deform:
                case AssetModelerWorkspace.SurfaceFit:
                    return true;
                default:
                    return false;
            }
        }
    }
}
