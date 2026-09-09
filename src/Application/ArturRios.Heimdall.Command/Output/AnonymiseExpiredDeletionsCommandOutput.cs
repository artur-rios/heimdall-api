using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.AnonymiseExpiredDeletionsCommand" />: how many identities the run
///     anonymised. Zero is an ordinary successful run.
/// </summary>
public class AnonymiseExpiredDeletionsCommandOutput : CommandOutput
{
    /// <summary>Persons anonymised.</summary>
    public int PersonsAnonymised { get; set; }

    /// <summary>Google Users anonymised.</summary>
    public int GoogleUsersAnonymised { get; set; }

    /// <summary>Dependent rows removed alongside them — tokens and two-factor configurations.</summary>
    public int DependentsRemoved { get; set; }

    /// <summary>Identities anonymised across both tables.</summary>
    public int TotalAnonymised => PersonsAnonymised + GoogleUsersAnonymised;
}
