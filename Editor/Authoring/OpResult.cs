using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public sealed class OpResult
    {
        public bool Ok;
        public string Error;
        public int InstanceID;
        public string Summary;
        public JToken Data;

        public string ExceptionType;
        public string StackTrace;

        /// Set by handlers that create a new asset on disk (e.g.
        /// `create_animator_controller`). Used by ChangesetExecutor to
        /// delete the file if a later op in the batch fails.
        public string CreatedAssetPath;

        public static OpResult Success(int instanceId, string summary)
            => new OpResult { Ok = true, InstanceID = instanceId, Summary = summary };

        public static OpResult Fail(string error)
            => new OpResult { Ok = false, Error = error };
    }
}
