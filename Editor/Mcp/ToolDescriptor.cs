using System;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    [Flags]
    public enum McpCapability
    {
        None = 0,
        Read = 1 << 0,
        SceneWrite = 1 << 1,
        AssetWrite = 1 << 2,
        ProjectSettings = 1 << 3,
        PackageManager = 1 << 4,
        Build = 1 << 5,
        PlayControl = 1 << 6,
        RuntimeCommand = 1 << 7,
        Reflection = 1 << 8,
        Destructive = 1 << 9,
    }

    public enum McpRiskLevel { Low, Medium, High, Critical }
    public enum McpConfirmationPolicy { None, Required }
    public enum McpEditorMode { Any, EditModeOnly, PlayModeOnly }

    public sealed class ToolDescriptor
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public McpCapability Capabilities { get; set; }
        public McpRiskLevel Risk { get; set; }
        public McpConfirmationPolicy ConfirmationPolicy { get; set; }
        public McpEditorMode EditorMode { get; set; }
        public bool IsIdempotent { get; set; }
        public JObject OutputSchema { get; set; }

        public bool IsWrite => (Capabilities & ~McpCapability.Read) != 0;

        public static ToolDescriptor Infer(IMcpTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            var name = tool.Name ?? string.Empty;
            var d = new ToolDescriptor
            {
                Name = name,
                Category = CategoryOf(name),
                Capabilities = tool.IsWrite ? McpCapability.SceneWrite : McpCapability.Read,
                Risk = tool.IsWrite ? McpRiskLevel.Medium : McpRiskLevel.Low,
                ConfirmationPolicy = McpConfirmationPolicy.None,
                EditorMode = tool.IsWrite ? McpEditorMode.EditModeOnly : McpEditorMode.Any,
                IsIdempotent = !tool.IsWrite || IsSetLike(name),
            };

            if (tool.IsWrite && IsAssetWrite(name)) d.Capabilities |= McpCapability.AssetWrite;
            if (IsPlayControl(name))
            {
                d.Capabilities = McpCapability.PlayControl;
                d.Risk = McpRiskLevel.Medium;
                d.EditorMode = McpEditorMode.Any;
            }
            if (name.StartsWith("send_runtime_", StringComparison.Ordinal) || name.StartsWith("bridge_", StringComparison.Ordinal))
                d.Capabilities = name == "bridge_status" || name == "list_bridge_handlers"
                    ? McpCapability.Read
                    : McpCapability.RuntimeCommand;
            if (name == "send_runtime_command" || name == "send_runtime_commands" || name == "list_bridge_handlers")
                d.EditorMode = McpEditorMode.PlayModeOnly;
            else if (IsEditModeOnly(name))
                d.EditorMode = McpEditorMode.EditModeOnly;

            if (name == "execute_menu_item" || name == "invoke_static_method" || name == "bind_button_click")
            {
                d.Capabilities = McpCapability.Reflection;
                d.Risk = McpRiskLevel.Critical;
                d.ConfirmationPolicy = McpConfirmationPolicy.Required;
            }
            else if (IsDestructive(name))
            {
                d.Capabilities |= McpCapability.Destructive;
                d.Risk = McpRiskLevel.High;
                d.ConfirmationPolicy = McpConfirmationPolicy.Required;
            }
            else if (IsPackageOperation(name))
            {
                d.Capabilities = McpCapability.PackageManager;
                d.Risk = McpRiskLevel.Critical;
                d.ConfirmationPolicy = McpConfirmationPolicy.Required;
            }
            else if (IsBuildOperation(name))
            {
                d.Capabilities = McpCapability.Build;
                d.Risk = McpRiskLevel.Critical;
                d.ConfirmationPolicy = McpConfirmationPolicy.Required;
            }
            else if (IsProjectSettingsOperation(name))
            {
                d.Capabilities = McpCapability.ProjectSettings;
                d.Risk = McpRiskLevel.High;
                d.ConfirmationPolicy = McpConfirmationPolicy.Required;
            }

            return d;
        }

        private static string CategoryOf(string name)
        {
            int split = name.IndexOf('_');
            return split > 0 ? name.Substring(0, split) : "general";
        }

        private static bool IsSetLike(string name) =>
            name.StartsWith("set_", StringComparison.Ordinal) ||
            name.StartsWith("read_", StringComparison.Ordinal) ||
            name.StartsWith("list_", StringComparison.Ordinal) ||
            name.StartsWith("get_", StringComparison.Ordinal) ||
            name.StartsWith("validate_", StringComparison.Ordinal) ||
            name.StartsWith("describe_", StringComparison.Ordinal);

        private static bool IsPackageOperation(string name) =>
            name == "confirm_add_package" || name == "confirm_remove_package";

        private static bool IsBuildOperation(string name) =>
            name == "start_build" || name == "cancel_build_job";

        private static bool IsProjectSettingsOperation(string name) =>
            name == "apply_project_settings_change" || name == "set_quality_settings" ||
            name == "set_physics2d_settings";

        private static bool IsAssetWrite(string name) =>
            name.Contains("asset") || name.Contains("scriptable_object") ||
            name.Contains("animator_controller") || name.Contains("timeline") ||
            name.Contains("post_effect") || name.Contains("volume") ||
            name.Contains("prefab") || name.Contains("import_settings") ||
            name.Contains("asset") || name.Contains("material") || name.Contains("terrain") ||
            name.Contains("avatar_mask") || name.Contains("animation_float_curve") ||
            name == "save_scene" || name == "save_all_scenes" ||
            name.Contains("panel_settings") || name.Contains("input_action") ||
            name.Contains("input_binding") || name.Contains("input_control_scheme") ||
            name.Contains("addressable") || name.Contains("localization") || name.Contains("localized_") || name.Contains("shader_graph") ||
            name == "set_so_property" || name == "modify_array_property" ||
            name == "create_folder" || name == "refresh_assets" ||
            name == "request_script_compilation" || name == "mesh_enable_read_write";

        private static bool IsPlayControl(string name) =>
            name == "enter_play_mode" || name == "exit_play_mode" ||
            name == "set_play_paused" || name == "step_frame" || name == "set_time_scale" ||
            name == "run_tests" || name == "cancel_test_job";

        private static bool IsEditModeOnly(string name) =>
            name.StartsWith("create_", StringComparison.Ordinal) ||
            name.StartsWith("configure_", StringComparison.Ordinal) ||
            name.StartsWith("bind_", StringComparison.Ordinal) ||
            name.StartsWith("write_", StringComparison.Ordinal) ||
            name.StartsWith("apply_", StringComparison.Ordinal) ||
            name.StartsWith("confirm_", StringComparison.Ordinal) ||
            name.StartsWith("preview_", StringComparison.Ordinal) ||
            name.StartsWith("reimport_", StringComparison.Ordinal) ||
            name.StartsWith("make_assets_", StringComparison.Ordinal) ||
            name == "set_material_property_typed" || name == "set_static_editor_flags" ||
            name == "set_tilemap_cells" || name == "set_terrain_heights" ||
            name == "set_rect_transform" || name == "set_physics2d_settings" ||
            name.StartsWith("add_input_", StringComparison.Ordinal) ||
            name.StartsWith("set_animation_", StringComparison.Ordinal);

        private static bool IsDestructive(string name) =>
            name == "apply_changeset" ||
            name.StartsWith("delete_", StringComparison.Ordinal) ||
            name == "confirm_delete_assets" || name == "close_scene" || name == "write_ui_asset" ||
            name.StartsWith("remove_", StringComparison.Ordinal) ||
            name.StartsWith("cleanup_", StringComparison.Ordinal) ||
            name.StartsWith("clear_", StringComparison.Ordinal) ||
            name.StartsWith("fix_missing", StringComparison.Ordinal) ||
            name.Contains("unpack_prefab");
    }
}
