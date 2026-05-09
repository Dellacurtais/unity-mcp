namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public sealed class OpResult
    {
        public bool Ok;
        public string Error;
        public int InstanceID;
        public string Summary;

        public static OpResult Success(int instanceId, string summary)
            => new OpResult { Ok = true, InstanceID = instanceId, Summary = summary };

        public static OpResult Fail(string error)
            => new OpResult { Ok = false, Error = error };
    }
}
