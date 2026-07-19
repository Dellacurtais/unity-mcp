using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public sealed class CoderStreamEvent
    {
        public string Type;
        public string Data;
        public string LastEventId;
        public JToken Payload;

        public bool IsText => Type == "text";
        public bool IsTerminal => Type == "done" || Type == "message_complete" || Type == "error";
        public string TextDelta => IsText ? (string)Payload?["text"] ?? Data ?? "" : "";

        public static CoderStreamEvent Create(string type, string data, string lastEventId)
        {
            JToken payload = null;
            if (!string.IsNullOrEmpty(data))
            {
                try { payload = JToken.Parse(data); }
                catch { }
            }
            return new CoderStreamEvent
            {
                Type = string.IsNullOrEmpty(type) ? "message" : type,
                Data = data ?? "",
                LastEventId = lastEventId,
                Payload = payload,
            };
        }

        public static List<CoderStreamEvent> CoalesceAdjacentText(IReadOnlyList<CoderStreamEvent> source)
        {
            var result = new List<CoderStreamEvent>();
            if (source == null) return result;
            StringBuilder text = null;
            string lastEventId = null;

            void FlushText()
            {
                if (text == null) return;
                var value = text.ToString();
                result.Add(new CoderStreamEvent
                {
                    Type = "text",
                    Data = value,
                    LastEventId = lastEventId,
                    Payload = new JObject { ["text"] = value },
                });
                text = null;
                lastEventId = null;
            }

            for (var index = 0; index < source.Count; index++)
            {
                var item = source[index];
                if (item != null && item.IsText)
                {
                    text ??= new StringBuilder();
                    text.Append(item.TextDelta);
                    if (!string.IsNullOrEmpty(item.LastEventId)) lastEventId = item.LastEventId;
                    continue;
                }
                FlushText();
                if (item != null) result.Add(item);
            }
            FlushText();
            return result;
        }
    }
}
