namespace GlitchPort.Processor.Projects;

/// <summary>A project without its aggregate counters.</summary>
public sealed record ProjectRecord(
    long Id,
    string Name,
    string Slug,
    string? Platform,
    DateTimeOffset CreatedAt);

/// <summary>
/// A project together with the counters shown by the project list:
/// unresolved issues and active keys.
/// </summary>
public sealed record ProjectSummary(
    long Id,
    string Name,
    string Slug,
    string? Platform,
    DateTimeOffset CreatedAt,
    long UnresolvedIssueCount,
    long ActiveKeyCount);

/// <summary>A public key of a project, including revoked keys.</summary>
public sealed record ProjectKeyRecord(
    long Id,
    long ProjectId,
    string PublicKey,
    string? Label,
    bool IsActive,
    int? RateLimitPerMinute,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

/// <summary>The project returned by a create, with the key created alongside it.</summary>
public sealed record CreatedProject(ProjectRecord Project, ProjectKeyRecord Key);

/// <summary>The relay configuration of a project.</summary>
public sealed record ProjectConfig(
    long Id,
    string Name,
    string Slug,
    string? Platform,
    IReadOnlyList<ProjectKeyConfig> Keys);

/// <summary>An active public key as exposed by the relay configuration.</summary>
public sealed record ProjectKeyConfig(
    long Id,
    string PublicKey,
    string? Label,
    int? RateLimitPerMinute);
