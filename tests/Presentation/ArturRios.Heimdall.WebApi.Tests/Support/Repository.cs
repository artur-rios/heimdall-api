namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Paths into the repository, for the tests that check the source, the scripts or the documents
///     against the code.
/// </summary>
/// <remarks>
///     The root is found by walking up from the test assembly to the directory holding
///     <c>src/ArturRios.Heimdall.sln</c>, not by a fixed number of <c>..</c> steps: the assembly is not
///     always in <c>bin/Release/net10.0</c>. ci/test.sh runs this project in shards, each from a copy of
///     the build output in <c>bin/shard-N</c>, where a fixed climb would end above the repository.
/// </remarks>
public static class Repository
{
    private static readonly Lazy<string> s_root = new(FindRoot);

    /// <summary>The repository's root directory.</summary>
    public static string Root => s_root.Value;

    /// <summary>A path under the repository's root.</summary>
    public static string PathTo(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src", "ArturRios.Heimdall.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}");
    }
}
