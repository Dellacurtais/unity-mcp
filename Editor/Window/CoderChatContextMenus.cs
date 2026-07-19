using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    internal static class CoderChatContextMenus
    {
        private const string AssetsMenu = "Assets/Anexar ao Coder Chat";
        private const string GameObjectMenu = "GameObject/Anexar ao Coder Chat";
        private const string ComponentMenu = "CONTEXT/Component/Anexar ao Coder Chat";

        [MenuItem(AssetsMenu, false, 2000)]
        private static void AttachAssets()
        {
            CoderChatWindow.AttachObjects(Selection.objects);
        }

        [MenuItem(AssetsMenu, true)]
        private static bool ValidateAttachAssets()
        {
            return Selection.objects != null && Selection.objects.Any(target => target != null);
        }

        [MenuItem(GameObjectMenu, false, 49)]
        private static void AttachGameObjects(MenuCommand command)
        {
            if (command?.context is GameObject context)
                CoderChatWindow.AttachObjects(new Object[] { context });
            else
                CoderChatWindow.AttachObjects(Selection.gameObjects.Cast<Object>());
        }

        [MenuItem(GameObjectMenu, true)]
        private static bool ValidateAttachGameObjects()
        {
            return Selection.gameObjects != null && Selection.gameObjects.Length > 0;
        }

        [MenuItem(ComponentMenu, false, 1000)]
        private static void AttachComponent(MenuCommand command)
        {
            if (command?.context is Component component)
                CoderChatWindow.AttachObjects(new Object[] { component });
        }
    }
}
