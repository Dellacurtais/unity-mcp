using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var excludedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".git", ".mcp-indexer", ".validation", "Library", "Temp", "obj", "bin"
};
var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !Path.GetRelativePath(root, path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(excludedSegments.Contains))
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();
var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
var errors = new List<Diagnostic>();
foreach (var file in files)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file);
    errors.AddRange(tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
}
foreach (var error in errors)
    Console.Error.WriteLine(error.ToString());
Console.WriteLine($"C# syntax: {files.Length} files, {errors.Count} errors");
return errors.Count == 0 ? 0 : 1;
