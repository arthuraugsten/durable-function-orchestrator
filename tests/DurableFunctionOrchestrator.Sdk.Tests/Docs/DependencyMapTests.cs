using System.Text.RegularExpressions;

namespace DurableFunctionOrchestrator.Sdk.Tests;

/// <summary>
/// Keeps <c>docs/sdk-dependency-map.html</c> in sync with the code. The map's <c>DEPS</c> block must list,
/// for every class on the map, exactly the other classes on the map that its source file references
/// (comments and string literals don't count). When a dependency changes, these tests fail with the
/// arrows to add or remove.
/// </summary>
public sealed partial class DependencyMapTests
{
  private const string MapRelativePath = "docs/sdk-dependency-map.html";

  private static readonly string RepoRoot = FindRepoRoot();

  [Fact]
  public void Every_SDK_class_is_on_the_dependency_map_and_every_mapped_class_has_a_source_file()
  {
    IReadOnlyDictionary<string, HashSet<string>> map = ReadMap();
    IReadOnlyDictionary<string, string> sources = SourceFiles();

    string[] unmapped = SdkFiles().Keys.Where(type => !map.ContainsKey(type)).Order(StringComparer.Ordinal).ToArray();
    string[] orphaned = map.Keys.Where(type => !sources.ContainsKey(type)).Order(StringComparer.Ordinal).ToArray();

    Assert.True(
      unmapped.Length == 0 && orphaned.Length == 0,
      $"{MapRelativePath} is out of date." +
      (unmapped.Length > 0 ? $"\nAdd to NODES and DEPS (new SDK classes): {string.Join(", ", unmapped)}" : "") +
      (orphaned.Length > 0 ? $"\nRemove from NODES and DEPS (no source file any more): {string.Join(", ", orphaned)}" : ""));
  }

  [Fact]
  public void Dependency_map_arrows_match_the_type_references_in_the_code()
  {
    IReadOnlyDictionary<string, HashSet<string>> map = ReadMap();
    IReadOnlyDictionary<string, string> sources = SourceFiles();
    var problems = new List<string>();

    foreach ((string type, HashSet<string> mapped) in map.OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
      if (!sources.TryGetValue(type, out string? path))
        continue; // Reported by the test above.

      string code = StripCommentsAndStrings(File.ReadAllText(path));
      HashSet<string> actual = [.. map.Keys.Where(other => other != type && Regex.IsMatch(code, $@"\b{other}\b"))];

      problems.AddRange(actual.Except(mapped).Order(StringComparer.Ordinal).Select(to => $"  add    {type} → {to}"));
      problems.AddRange(mapped.Except(actual).Order(StringComparer.Ordinal).Select(to => $"  remove {type} → {to}"));
    }

    Assert.True(
      problems.Count == 0,
      $"The DEPS block in {MapRelativePath} doesn't match the code. Update it (and any notes on the page that mention these arrows):\n" +
      string.Join("\n", problems));
  }

  // DEPS entries look like:   StepStage: ["IPipelineStage", "PipelineStep", ...],
  private static IReadOnlyDictionary<string, HashSet<string>> ReadMap()
  {
    string html = File.ReadAllText(Path.Combine(RepoRoot, MapRelativePath));
    Match block = DependenciesBlock().Match(html);
    Assert.True(block.Success, $"{MapRelativePath} must keep its DEPS between '// <dependencies>' and '// </dependencies>'.");

    var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (Match entry in DependencyEntry().Matches(block.Groups[1].Value))
      map[entry.Groups[1].Value] = [.. QuotedName().Matches(entry.Groups[2].Value).Select(name => name.Groups[1].Value)];

    Assert.NotEmpty(map);
    return map;
  }

  // Classes the map can show: every SDK file, plus the example app's Orders files.
  private static IReadOnlyDictionary<string, string> SourceFiles()
  {
    var files = new Dictionary<string, string>(SdkFiles(), StringComparer.Ordinal);
    foreach (string path in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "DurableFunctionOrchestrator", "Orders"), "*.cs"))
      files.TryAdd(Path.GetFileNameWithoutExtension(path), path);

    return files;
  }

  // One type per file, so the file name is the class name.
  private static Dictionary<string, string> SdkFiles() =>
    Directory
      .EnumerateFiles(Path.Combine(RepoRoot, "src", "DurableFunctionOrchestrator.Sdk", "Orchestrator"), "*.cs", SearchOption.AllDirectories)
      .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

  private static string StripCommentsAndStrings(string code) =>
    StringLiteral().Replace(LineComment().Replace(code, ""), "\"\"");

  private static string FindRepoRoot()
  {
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
      if (File.Exists(Path.Combine(dir.FullName, "DurableFunctionOrchestrator.slnx")))
        return dir.FullName;
    }

    throw new InvalidOperationException("Could not find the repository root (DurableFunctionOrchestrator.slnx) above the test output folder.");
  }

  [GeneratedRegex(@"// <dependencies>(.*?)// </dependencies>", RegexOptions.Singleline)]
  private static partial Regex DependenciesBlock();

  [GeneratedRegex(@"^\s*(\w+):\s*\[(.*?)\]", RegexOptions.Multiline)]
  private static partial Regex DependencyEntry();

  [GeneratedRegex(@"""(\w+)""")]
  private static partial Regex QuotedName();

  [GeneratedRegex(@"//[^\n]*")]
  private static partial Regex LineComment();

  [GeneratedRegex(@"\$?@?""(?:[^""\\]|\\.)*""")]
  private static partial Regex StringLiteral();
}
