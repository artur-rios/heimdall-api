using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     UC-54's answer: every credential the application holds, newest first. Not paginated — an
///     application holds at most two live credentials, and spent ones are purged after the
///     single-use-token grace, so the list stays a handful of rows.
/// </summary>
public class ApplicationCredentialListOutput : QueryOutput
{
    public List<ApplicationCredentialOutput> Credentials { get; set; } = [];
}
