using System.Reflection;
using System.Text.RegularExpressions;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     NFR-22's redaction half, enforced over the source rather than over a captured log.
/// </summary>
/// <remarks>
///     <para>
///         A behavioural test would cover the statements it happened to exercise and say nothing
///         about the next one somebody adds. The rule is about the whole codebase — no log statement
///         writes an address — so it is checked the way the rule is stated.
///     </para>
///     <para>
///         The check is deliberately crude: a message template with an <c>{Email}</c> placeholder.
///         It catches the reintroduction this is guarding against, which is somebody writing the
///         obvious thing, and it is why the placeholder used everywhere is <c>{EmailRef}</c>.
///     </para>
/// </remarks>
public partial class LogRedactionTests
{
    [GeneratedRegex(@"\{Email\}")]
    private static partial Regex RawEmailPlaceholder();

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "..", "..", "..", "..", "..", "..", "src"));

        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                               && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                               && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            : [];
    }

    [UnitFact]
    public void GivenTheSource_WhenLogStatementsAreRead_ThenNoneWritesAnEmailAddress()
    {
        var files = SourceFiles().ToList();

        // If the layout moves and the scan finds nothing, that is a failing test rather than a
        // passing one: a check that silently stops checking is worse than no check.
        Assert.NotEmpty(files);

        var offenders = files
            .Where(path => RawEmailPlaceholder().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These log statements write an email address; use LogSafeEmail.Reference and " +
            $"{{EmailRef}} instead: {string.Join(", ", offenders)}");
    }
}
