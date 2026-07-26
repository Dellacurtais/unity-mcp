namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public static class ToolAuthorization
    {
        public static bool TryAuthorize(IMcpTool tool, ToolDescriptor descriptor, out string code, out string message)
        {
            code = null;
            message = null;
            if (tool == null) { code = "TOOL_NOT_FOUND"; message = "Tool is not registered."; return false; }
            descriptor = descriptor ?? ToolDescriptor.Infer(tool);

            if (descriptor.EditorMode == McpEditorMode.EditModeOnly
                && UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return Deny("EDIT_MODE_REQUIRED", $"Tool '{tool.Name}' is only available in Edit Mode.", out code, out message);
            if (descriptor.EditorMode == McpEditorMode.PlayModeOnly
                && !UnityEditor.EditorApplication.isPlaying)
                return Deny("PLAY_MODE_REQUIRED", $"Tool '{tool.Name}' is only available in Play Mode.", out code, out message);
            if (descriptor.IsWrite && !Settings.AllowWrites)
                return Deny("WRITE_DISABLED", "Writes are disabled in the Unity MCP Transport panel.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.Reflection) != 0 && !Settings.AllowReflectionTools)
                return Deny("CAPABILITY_DISABLED", "Reflection tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.Destructive) != 0 && !Settings.AllowDestructiveTools)
                return Deny("CAPABILITY_DISABLED", "Destructive tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.PackageManager) != 0 && !Settings.AllowPackageTools)
                return Deny("CAPABILITY_DISABLED", "Package Manager tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.Build) != 0 && !Settings.AllowBuildTools)
                return Deny("CAPABILITY_DISABLED", "Build tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.ProjectSettings) != 0 && !Settings.AllowProjectSettingsTools)
                return Deny("CAPABILITY_DISABLED", "Project settings tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.PlayControl) != 0 && !Settings.AllowPlayControlTools)
                return Deny("CAPABILITY_DISABLED", "Play control tools are disabled in Unity MCP settings.", out code, out message);
            if ((descriptor.Capabilities & McpCapability.RuntimeCommand) != 0 && !Settings.AllowRuntimeCommandTools)
                return Deny("CAPABILITY_DISABLED", "Runtime command tools are disabled in Unity MCP settings.", out code, out message);
            return true;
        }

        public static bool RequiresConfirmation(ToolDescriptor descriptor) =>
            Settings.RequireHighRiskConfirmation && descriptor != null &&
            descriptor.ConfirmationPolicy == McpConfirmationPolicy.Required;

        private static bool Deny(string deniedCode, string deniedMessage, out string code, out string message)
        {
            code = deniedCode;
            message = deniedMessage;
            return false;
        }
    }
}
