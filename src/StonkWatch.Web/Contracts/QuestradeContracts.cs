namespace StonkWatch.Web.Contracts;

/// <summary>Connected means a live Questrade session could be obtained just now.</summary>
public record QuestradeStatusDto(bool Connected, string? Reason);

/// <summary>
/// <paramref name="RefreshToken"/> is deliberately <c>string</c>, not <c>string?</c> — the
/// endpoint still has to tolerate a JSON <c>null</c> at runtime the same way every other
/// request record in this app does, but the type documents what a well-formed call looks like.
/// </summary>
public record AuthorizeQuestradeRequest(string RefreshToken);
