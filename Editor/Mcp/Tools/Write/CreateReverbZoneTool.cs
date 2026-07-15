using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateReverbZoneTool : IMcpTool
    {
        public string Name => "create_reverb_zone";
        public string Description =>
            "Creates an AudioReverbZone at a position with min/max distance and a preset. " +
            "Presets: Generic, PaddedCell, Room, Bathroom, Livingroom, Stoneroom, Auditorium, ConcertHall, Cave, Arena, Hangar, " +
            "CarpetedHallway, Hallway, StoneCorridor, Alley, Forest, City, Mountains, Quarry, Plain, ParkingLot, SewerPipe, " +
            "Underwater, Drugged, Dizzy, Psychotic, User. Inside minDistance: full reverb. Outside maxDistance: none. Between: blended.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["minDistance"] = new JObject { ["type"] = "number", ["default"] = 10 },
                ["maxDistance"] = new JObject { ["type"] = "number", ["default"] = 15 },
                ["preset"] = new JObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.CreateReverbZone(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
