using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    public interface IAnalyzer
    {
        string Id { get; }
        string Category { get; }
        IEnumerable<Issue> Run(SceneSnapshot snapshot);
    }
}
