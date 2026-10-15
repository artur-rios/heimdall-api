namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     A collection that runs alone, for the one test that temporarily replaces
///     <c>PasswordHashGate.Shared</c>, so no concurrently running test derives through its gate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PasswordHashGateCollection
{
    public const string Name = "PasswordHashGate.Shared";
}
