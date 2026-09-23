using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var excludedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".git", ".mcp-indexer", ".validation", ".pytest_cache", ".venv", "venv", "__pycache__",
    "Library", "Temp", "obj", "bin"
};
// Prune before descending. Filtering an AllDirectories enumeration afterward
// still traverses generated projects, environments and inaccessible caches.
IEnumerable<string> SourceFiles(string directory)
{
    foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        yield return file;
    foreach (var child in Directory.EnumerateDirectories(directory))
    {
        if (excludedSegments.Contains(Path.GetFileName(child))) continue;
        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Source traversal encountered a linked directory: {child}. Resolve it explicitly; do not silently omit source files.");
        foreach (var file in SourceFiles(child)) yield return file;
    }
}
var files = SourceFiles(root)
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
