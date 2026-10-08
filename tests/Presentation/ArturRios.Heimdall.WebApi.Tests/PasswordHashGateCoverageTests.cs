using System.Reflection;
using System.Text.RegularExpressions;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Threat Model TH-03's bound, enforced over the source: every Argon2id derivation on a request
///     path goes through <c>PasswordHashGate</c>.
/// </summary>
/// <remarks>
///     The gate's own documentation states the rule — "a bound with a way around it is not a bound" —
///     and the two-factor email codes were the way around it: hashed and compared with the same
///     600 MB parameters as a password, directly, on every login, resend and verification. Checked
///     the way <c>LogRedactionTests</c> checks NFR-22, because the next direct call is the one a
///     behavioural test would not know to exercise.
/// </remarks>
public partial class PasswordHashGateCoverageTests
{
    // The places allowed to derive directly, each for a reason PasswordHashGate's remarks give:
    // the gate itself; the login decoy, computed once at type initialisation; and the seeder's
    // master user, written at start-up before anything is served.
    private static readonly string[] Allowed =
        ["PasswordHashGate.cs", "LoginCommandHandler.cs", "DatabaseSeeder.cs"];

    [GeneratedRegex(@"\bHash\.(EncodeWithRandomSalt|EncodeWithSalt|TextMatches)\(")]
    private static partial Regex DirectDerivation();

    [UnitFact]
    public void GivenTheSource_WhenArgon2DerivationsAreRead_ThenEachGoesThroughTheGate()
    {
        var root = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "..", "..", "..", "..", "..", "..", "src"));

        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // A scan that silently finds nothing is a failing check, not a passing one.
        Assert.NotEmpty(files);

        var offenders = files
            .Where(path => !Allowed.Contains(Path.GetFileName(path)))
            .Where(path => DirectDerivation().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files derive an Argon2id hash outside PasswordHashGate; use " +
            $"PasswordHashGate.Shared instead: {string.Join(", ", offenders)}");
    }
}
