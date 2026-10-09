namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Marks an action through which a data subject exercises a right over their own identity —
///     export (UC-41), erasure (UC-42), restriction (UC-44), and lifting their own restriction
///     (UC-45). On these actions alone, <see cref="ActorLivenessFilter" /> lets through a token naming
///     an identity that is restricted (NFR-24) or logically deleted but not yet anonymised.
/// </summary>
/// <remarks>
///     <para>
///         Everywhere else the filter refuses such a token, and it has to: a restricted identity may
///         not be processed, and a suspended one is on its way out. But both are still somebody's
///         data, and the rights over it do not lapse with the account's standing. NFR-24 keeps a
///         restricted identity "reachable by its own subject's export", UC-45 lets "the subject" lift
///         their own restriction, UC-41 and UC-44 say a suspended identity may still export and
///         restrict, and the DPIA (R-06) counts erasure among the rights that must not need an
///         administrator. A filter that refused all of them turned every one into a right that
///         existed on paper only — and let a stolen token impose a restriction that only a System
///         Admin could then undo.
///     </para>
///     <para>
///         The handlers behind these actions resolve the subject from the token and already define
///         what a non-live caller gets — AF-41a, AF-42b, AF-44a, AF-44c, AF-45a/c — so the filter
///         defers to them rather than answering first with its own 401. It still refuses an identity
///         that does not exist or has been anonymised, and it still checks the role claim (TH-08).
///     </para>
///     <para>
///         Only actions whose subject is always the caller may carry this. The lift endpoint also
///         serves a System Admin lifting somebody else's restriction; that path is the handler's to
///         refuse for a caller who is themselves restricted or suspended, and it does.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DataSubjectRightAttribute : Attribute;
