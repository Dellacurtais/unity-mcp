using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    /// Provider entry returned by `GET /api/coder/providers`.
    public sealed class CoderProviderInfo
    {
        public string Id;
        public string Name;
        public string Kind;
        public bool IsDefault;
        public bool HasKey;
        public List<CoderModelInfo> Models = new();
    }

    public sealed class CoderModelInfo
    {
        public string Id;
        public string Name;
        public string Mode;
        public int? ContextWindow;
        public int? DefaultMaxTokens;
        public bool CanReason;
        public List<string> SupportedReasoningLevels = new();
        public string DefaultReasoningLevel;
    }
}
