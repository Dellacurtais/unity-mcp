using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public sealed class CoderAgentInfo
    {
        public string Id;
        public string Label;
        public string Description;
        public string Profile;
        public bool PlanMode;
        public bool SpecMode;
        public int? MaxTurns;
        public float? DefaultTemperature;
        public bool ConfirmBeforeIrreversible;

        public static CoderAgentInfo FromJson(JToken token)
        {
            if (token == null) return null;
            return new CoderAgentInfo
            {
                Id = (string)token["id"],
                Label = (string)token["label"],
                Description = (string)token["description"],
                Profile = (string)token["profile"],
                PlanMode = (bool?)token["planMode"] ?? false,
                SpecMode = (bool?)token["specMode"] ?? false,
                MaxTurns = (int?)token["maxTurns"],
                DefaultTemperature = (float?)token["defaultTemperature"],
                ConfirmBeforeIrreversible = (bool?)token["confirmBeforeIrreversible"] ?? false,
            };
        }

        public override string ToString() => string.IsNullOrEmpty(Label) ? Id ?? "Agente" : Label;
    }
}
