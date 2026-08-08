using System;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneUx
{
    [EditorToolContext("Asset Modeler", typeof(GameObject))]
    internal sealed class AssetModelerToolContext : EditorToolContext
    {
        public static bool IsActive => ToolManager.activeContextType == typeof(AssetModelerToolContext);

        protected override Type GetEditorToolType(Tool tool) => typeof(AssetModelerModelingTool);

        public override void OnActivated()
        {
            base.OnActivated();
            UnityEditor.SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            base.OnWillBeDeactivated();
            UnityEditor.SceneView.RepaintAll();
        }
    }
}
