using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal sealed class Ai3dWorkflowNodePlan
    {
        public string Id;
        public string Operation;
        public int EstimatedCredits;
        public int BudgetCapCredits;
        public JObject Payload = new JObject();

        public JObject ToJson(bool includePayload = true)
        {
            var result = new JObject
            {
                ["id"] = Id,
                ["operation"] = Operation,
                ["estimatedCredits"] = EstimatedCredits,
                ["budgetCapCredits"] = BudgetCapCredits,
            };
            if (includePayload) result["payload"] = Payload?.DeepClone() ?? new JObject();
            return result;
        }

        public static Ai3dWorkflowNodePlan Parse(JToken token)
        {
            if (!(token is JObject json)) return null;
            var id = (string)json["id"];
            var operation = (string)json["operation"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(operation)) return null;
            return new Ai3dWorkflowNodePlan
            {
                Id = id,
                Operation = operation,
                EstimatedCredits = Math.Max(0, (int?)json["estimatedCredits"] ?? 0),
                BudgetCapCredits = Math.Max(0, (int?)json["budgetCapCredits"] ?? 0),
                Payload = (json["payload"] as JObject)?.DeepClone() as JObject ?? new JObject(),
            };
        }

        public static List<Ai3dWorkflowNodePlan> ParseMany(JArray values)
        {
            var result = new List<Ai3dWorkflowNodePlan>();
            foreach (var value in values ?? new JArray())
            {
                var node = Parse(value);
                if (node != null) result.Add(node);
            }
            return result;
        }

        public static JArray ToJson(IReadOnlyList<Ai3dWorkflowNodePlan> nodes, bool includePayload = true)
        {
            var result = new JArray();
            if (nodes == null) return result;
            for (int i = 0; i < nodes.Count; i++) result.Add(nodes[i].ToJson(includePayload));
            return result;
        }
    }
}
