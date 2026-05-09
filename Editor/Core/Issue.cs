using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    public enum IssueSeverity
    {
        Info,
        Warning,
        Error,
    }

    public sealed class Issue
    {
        public string Analyzer;
        public IssueSeverity Severity;
        public string Category;
        public string Message;
        public int InstanceID;
        public string GameObjectPath;
        public Dictionary<string, object> Data;

        public Issue(
            string analyzer,
            IssueSeverity severity,
            string category,
            string message,
            int instanceId = 0,
            string gameObjectPath = null,
            Dictionary<string, object> data = null)
        {
            Analyzer = analyzer;
            Severity = severity;
            Category = category;
            Message = message;
            InstanceID = instanceId;
            GameObjectPath = gameObjectPath;
            Data = data;
        }
    }
}
