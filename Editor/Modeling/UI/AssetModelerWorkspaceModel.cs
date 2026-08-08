using System;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    internal enum AssetModelerWorkspace
    {
        Home,
        Session,
        Objects,
        Topology,
        Deform,
        SurfaceFit,
    }

    internal enum AssetModelerObjectTool
    {
        Transform,
        Mirror,
        Array,
        Merge,
        Export,
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
            "Transformar", "Espelhar", "Array", "Combinar", "Exportar",
        };

        public static readonly string[] TopologyToolLabels =
        {
            "Limpar", "Soldar", "Subdividir", "Extrudar", "Bevel",
            "Refinar", "Extrudar aresta", "Bridge", "Fechar buraco",
        };

        public static string Label(AssetModelerWorkspace workspace)
        {
            switch (workspace)
            {
                case AssetModelerWorkspace.Session: return "Sessão editável";
                case AssetModelerWorkspace.Objects: return "Objetos e Transformações";
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
                case AssetModelerWorkspace.Objects: return "Espelhe, duplique em array, alinhe, combine ou exporte objetos da cena.";
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
                    return objectTool == AssetModelerObjectTool.Transform || objectTool == AssetModelerObjectTool.Mirror;
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
