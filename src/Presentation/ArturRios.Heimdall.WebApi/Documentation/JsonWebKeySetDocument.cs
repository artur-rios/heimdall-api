namespace ArturRios.Heimdall.WebApi.Documentation;

/// <summary>
///     The RFC 7517 key set <c>GET /.well-known/jwks.json</c> answers with: the public keys Heimdall's
///     tokens verify under, the current key first. Documentation only — the endpoint writes the set
///     from <c>TokenSigningKeys.JwksJson</c> and never serializes this type.
/// </summary>
/// <param name="Keys">The public keys: the current signing key, then the previous one during a rotation.</param>
public record JsonWebKeySetDocument(IReadOnlyList<JsonWebKeyDocument> Keys);

/// <summary>
///     One public key of the set. Documentation only. A key never carries a private member such as
///     <c>d</c>.
/// </summary>
/// <param name="Kty">The key type, always <c>EC</c>.</param>
/// <param name="Crv">The curve, always <c>P-256</c>.</param>
/// <param name="X">The public point's x coordinate, base64url without padding.</param>
/// <param name="Y">The public point's y coordinate, base64url without padding.</param>
/// <param name="Kid">
///     The key's RFC 7638 thumbprint, the <c>kid</c> in the header of every token it signed.
/// </param>
/// <param name="Use">The key's use, always <c>sig</c>.</param>
/// <param name="Alg">The algorithm, always <c>ES256</c>.</param>
public record JsonWebKeyDocument(string Kty, string Crv, string X, string Y, string Kid, string Use, string Alg);
