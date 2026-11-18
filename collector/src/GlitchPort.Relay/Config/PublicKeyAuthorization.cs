namespace GlitchPort.Relay.Config;

/// <summary>
/// The result of authorizing an ingest request with a public key. The optional
/// <see cref="RateLimitPerMinute"/> comes from the Processor project
/// configuration and is <see langword="null"/> when the Processor did not set a
/// limit for the key.
/// </summary>
public sealed record PublicKeyAuthorization(long ProjectId, string Key, int? RateLimitPerMinute);
