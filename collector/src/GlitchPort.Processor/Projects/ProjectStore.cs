using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Projects;

/// <summary>
/// Reads and writes the projects and public keys stored in PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized, and every query that returns a collection
/// orders it by id so that the management API and the dashboard see stable
/// results.
/// </para>
/// <para>
/// Public keys are random, so a create can collide with an existing key. The
/// key is regenerated and the statement retried
/// <see cref="MaxPublicKeyAttempts"/> times before the collision surfaces.
/// </para>
/// </remarks>
public sealed class ProjectStore
{
    /// <summary>How often a random public key is generated before giving up.</summary>
    public const int MaxPublicKeyAttempts = 5;

    private const string ListProjectsSql = """
        select p.id as "Id",
               p.name as "Name",
               p.slug as "Slug",
               p.created_at as "CreatedAt",
               p.filter_releases as "FilterReleases",
               p.filter_environments as "FilterEnvironments",
               p.filter_message_patterns as "FilterMessagePatterns",
               coalesce(issue_counts.unresolved_issue_count, 0) as "UnresolvedIssueCount",
               coalesce(key_counts.active_key_count, 0) as "ActiveKeyCount",
               coalesce(platform_stats.platforms, array[]::text[]) as "Platforms"
        from projects p
        left join (
            select project_id, count(*) as unresolved_issue_count
            from issues
            where status = 'unresolved'
            group by project_id
        ) as issue_counts on issue_counts.project_id = p.id
        left join (
            select project_id, count(*) as active_key_count
            from project_keys
            where is_active
            group by project_id
        ) as key_counts on key_counts.project_id = p.id
        left join lateral (
            select array_agg(
                       platform_rollup.platform
                       order by platform_rollup.event_count desc, platform_rollup.platform)
                   as platforms
            from (
                select i.platform, sum(i.event_count) as event_count
                from issues i
                where i.project_id = p.id
                group by i.platform
            ) as platform_rollup
        ) as platform_stats on true
        order by p.id
        """;

    private const string GetProjectSql = """
        select id as "Id",
               name as "Name",
               slug as "Slug",
               created_at as "CreatedAt"
        from projects
        where id = @ProjectId
        """;

    private const string GetProjectSummarySql = """
        select p.id as "Id",
               p.name as "Name",
               p.slug as "Slug",
               p.created_at as "CreatedAt",
               p.filter_releases as "FilterReleases",
               p.filter_environments as "FilterEnvironments",
               p.filter_message_patterns as "FilterMessagePatterns",
               coalesce(issue_counts.unresolved_issue_count, 0) as "UnresolvedIssueCount",
               coalesce(key_counts.active_key_count, 0) as "ActiveKeyCount",
               coalesce(platform_stats.platforms, array[]::text[]) as "Platforms"
        from projects p
        left join (
            select project_id, count(*) as unresolved_issue_count
            from issues
            where status = 'unresolved'
            group by project_id
        ) as issue_counts on issue_counts.project_id = p.id
        left join (
            select project_id, count(*) as active_key_count
            from project_keys
            where is_active
            group by project_id
        ) as key_counts on key_counts.project_id = p.id
        left join lateral (
            select array_agg(
                       platform_rollup.platform
                       order by platform_rollup.event_count desc, platform_rollup.platform)
                   as platforms
            from (
                select i.platform, sum(i.event_count) as event_count
                from issues i
                where i.project_id = p.id
                group by i.platform
            ) as platform_rollup
        ) as platform_stats on true
        where p.id = @ProjectId
        """;

    private const string ProjectExistsSql = "select exists (select 1 from projects where id = @ProjectId)";

    private const string InsertProjectSql = """
        insert into projects (name, slug)
        values (@Name, @Slug)
        returning id as "Id",
                  name as "Name",
                  slug as "Slug",
                  created_at as "CreatedAt"
        """;

    private const string UpdateProjectSql = """
        update projects
        set name = coalesce(@Name::text, name),
            filter_releases = coalesce(@Releases::text[], filter_releases),
            filter_environments = coalesce(@Environments::text[], filter_environments),
            filter_message_patterns = coalesce(@MessagePatterns::text[], filter_message_patterns)
        where id = @ProjectId
        returning id as "Id",
                  name as "Name",
                  slug as "Slug",
                  created_at as "CreatedAt"
        """;

    private const string DeleteProjectSql = "delete from projects where id = @ProjectId";

    private const string ListKeysSql = """
        select id as "Id",
               project_id as "ProjectId",
               public_key as "PublicKey",
               label as "Label",
               is_active as "IsActive",
               rate_limit_per_minute as "RateLimitPerMinute",
               created_at as "CreatedAt",
               revoked_at as "RevokedAt"
        from project_keys
        where project_id = @ProjectId
        order by id
        """;

    private const string InsertKeySql = """
        insert into project_keys (project_id, public_key, label, rate_limit_per_minute)
        values (@ProjectId, @PublicKey, @Label, @RateLimitPerMinute)
        returning id as "Id",
                  project_id as "ProjectId",
                  public_key as "PublicKey",
                  label as "Label",
                  is_active as "IsActive",
                  rate_limit_per_minute as "RateLimitPerMinute",
                  created_at as "CreatedAt",
                  revoked_at as "RevokedAt"
        """;

    private const string UpdateKeySql = """
        update project_keys
        set label = coalesce(@Label::text, label),
            rate_limit_per_minute = coalesce(@RateLimitPerMinute, rate_limit_per_minute),
            is_active = coalesce(@IsActive, is_active)
        where id = @KeyId and project_id = @ProjectId
        returning id as "Id",
                  project_id as "ProjectId",
                  public_key as "PublicKey",
                  label as "Label",
                  is_active as "IsActive",
                  rate_limit_per_minute as "RateLimitPerMinute",
                  created_at as "CreatedAt",
                  revoked_at as "RevokedAt"
        """;

    // A partial update must be able to clear the nullable rate limit, which
    // coalesce cannot express. The explicit "has" flags keep an omitted field
    // apart from an explicit null, and toggling is_active also maintains
    // revoked_at in the same statement.
    private const string UpdateKeyByIdSql = """
        update project_keys
        set label = case when @HasLabel then @Label::text else label end,
            rate_limit_per_minute = case
                when @HasRateLimit then @RateLimitPerMinute::integer
                else rate_limit_per_minute
            end,
            revoked_at = case
                when not @HasIsActive then revoked_at
                when @IsActive then null
                else now()
            end,
            is_active = case when @HasIsActive then @IsActive else is_active end
        where id = @KeyId
        returning id as "Id",
                  project_id as "ProjectId",
                  public_key as "PublicKey",
                  label as "Label",
                  is_active as "IsActive",
                  rate_limit_per_minute as "RateLimitPerMinute",
                  created_at as "CreatedAt",
                  revoked_at as "RevokedAt"
        """;

    private const string RevokeKeyByIdSql = """
        update project_keys
        set is_active = false,
            revoked_at = now()
        where id = @KeyId
        returning id as "Id",
                  project_id as "ProjectId",
                  public_key as "PublicKey",
                  label as "Label",
                  is_active as "IsActive",
                  rate_limit_per_minute as "RateLimitPerMinute",
                  created_at as "CreatedAt",
                  revoked_at as "RevokedAt"
        """;

    // Revoking keeps the row so that the management API and the dashboard can
    // still show which key was used before it was revoked.
    private const string RevokeKeySql = """
        update project_keys
        set is_active = false,
            revoked_at = now()
        where id = @KeyId and project_id = @ProjectId
        returning id as "Id",
                  project_id as "ProjectId",
                  public_key as "PublicKey",
                  label as "Label",
                  is_active as "IsActive",
                  rate_limit_per_minute as "RateLimitPerMinute",
                  created_at as "CreatedAt",
                  revoked_at as "RevokedAt"
        """;

    private const string ListConfigProjectsSql = """
        select id as "Id",
               name as "Name",
               slug as "Slug",
               filter_releases as "FilterReleases",
               filter_environments as "FilterEnvironments",
               filter_message_patterns as "FilterMessagePatterns"
        from projects
        order by id
        """;

    private const string ListActiveKeyConfigsSql = """
        select id as "Id",
               project_id as "ProjectId",
               public_key as "PublicKey",
               label as "Label",
               rate_limit_per_minute as "RateLimitPerMinute"
        from project_keys
        where is_active
        order by id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PublicKeyGenerator _publicKeyGenerator;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>
    /// and issues keys with <paramref name="publicKeyGenerator"/>.
    /// </summary>
    public ProjectStore(NpgsqlDataSource dataSource, PublicKeyGenerator publicKeyGenerator)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(publicKeyGenerator);

        _dataSource = dataSource;
        _publicKeyGenerator = publicKeyGenerator;
    }

    /// <summary>
    /// Lists all projects ordered by id with their unresolved issue and active
    /// key counts and the platforms of their issues.
    /// </summary>
    public async Task<List<ProjectSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<ProjectSummaryRow>(new CommandDefinition(
                ListProjectsSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(static row => row.ToSummary()).ToList();
    }

    /// <summary>
    /// Returns the project with <paramref name="projectId"/>, or <c>null</c>
    /// when it does not exist.
    /// </summary>
    public async Task<ProjectRecord?> GetAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                GetProjectSql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>
    /// Returns the project with <paramref name="projectId"/> together with its
    /// unresolved issue and active key counts, or <c>null</c> when it does not
    /// exist.
    /// </summary>
    public async Task<ProjectSummary?> GetSummaryAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectSummaryRow>(new CommandDefinition(
                GetProjectSummarySql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToSummary();
    }

    /// <summary>Checks whether a project with <paramref name="projectId"/> exists.</summary>
    public async Task<bool> ExistsAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await connection
            .ExecuteScalarAsync<bool>(new CommandDefinition(
                ProjectExistsSql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a project together with its first active public key in one
    /// transaction.
    /// </summary>
    /// <returns>
    /// The new project and key, or <c>null</c> when <paramref name="slug"/> is
    /// already used by another project.
    /// </returns>
    public async Task<CreatedProject?> CreateAsync(
        string name,
        string slug,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        PostgresException? lastCollision = null;

        for (var attempt = 1; attempt <= MaxPublicKeyAttempts; attempt++)
        {
            try
            {
                return await CreateWithKeyAsync(name, slug, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception) when (IsPublicKeyCollision(exception))
            {
                lastCollision = exception;
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a unique public key after {MaxPublicKeyAttempts} attempts.",
            lastCollision);
    }

    /// <summary>
    /// Changes the name of a project while keeping its inbound filters.
    /// </summary>
    /// <returns>The updated project, or <c>null</c> when it does not exist.</returns>
    public Task<ProjectRecord?> UpdateAsync(
        long projectId,
        string? name,
        CancellationToken cancellationToken) =>
        UpdateAsync(projectId, new ProjectUpdate(Name: name, default, default, default), cancellationToken);

    /// <summary>
    /// Changes the name and/or the inbound filters of a project. Unspecified
    /// fields keep their stored value; a specified list replaces the stored one.
    /// </summary>
    /// <returns>The updated project, or <c>null</c> when it does not exist.</returns>
    public async Task<ProjectRecord?> UpdateAsync(
        long projectId,
        ProjectUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                UpdateProjectSql,
                new
                {
                    ProjectId = projectId,
                    Name = update.Name,
                    Releases = ToArray(update.Releases),
                    Environments = ToArray(update.Environments),
                    MessagePatterns = ToArray(update.MessagePatterns),
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    private static string[]? ToArray(OptionalField<IReadOnlyList<string>> field) =>
        field.IsSpecified
            ? field.Value?.ToArray() ?? []
            : null;

    /// <summary>
    /// Deletes a project. Its keys, issues, events and labels are removed by
    /// the cascading foreign keys.
    /// </summary>
    /// <returns><c>true</c> when a project was deleted.</returns>
    public async Task<bool> DeleteAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                DeleteProjectSql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// Lists the keys of a project, revoked ones included, ordered by id.
    /// </summary>
    public async Task<List<ProjectKeyRecord>> ListKeysAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<ProjectKeyRow>(new CommandDefinition(
                ListKeysSql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(static row => row.ToRecord()).ToList();
    }

    /// <summary>
    /// Adds an active key to a project.
    /// </summary>
    /// <returns>The new key, or <c>null</c> when the project does not exist.</returns>
    public async Task<ProjectKeyRecord?> CreateKeyAsync(
        long projectId,
        string? label,
        int? rateLimitPerMinute,
        CancellationToken cancellationToken)
    {
        PostgresException? lastCollision = null;

        for (var attempt = 1; attempt <= MaxPublicKeyAttempts; attempt++)
        {
            var publicKey = _publicKeyGenerator.Generate();

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var row = await connection
                    .QuerySingleAsync<ProjectKeyRow>(new CommandDefinition(
                        InsertKeySql,
                        new
                        {
                            ProjectId = projectId,
                            PublicKey = publicKey,
                            Label = label,
                            RateLimitPerMinute = rateLimitPerMinute,
                        },
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

                return row.ToRecord();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                // The project does not exist, or was deleted concurrently.
                return null;
            }
            catch (PostgresException exception) when (IsPublicKeyCollision(exception))
            {
                lastCollision = exception;
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a unique public key after {MaxPublicKeyAttempts} attempts.",
            lastCollision);
    }

    /// <summary>
    /// Changes the label, rate limit and/or active flag of a key.
    /// </summary>
    /// <returns>The updated key, or <c>null</c> when it does not exist.</returns>
    public async Task<ProjectKeyRecord?> UpdateKeyAsync(
        long projectId,
        long keyId,
        string? label,
        int? rateLimitPerMinute,
        bool? isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectKeyRow>(new CommandDefinition(
                UpdateKeySql,
                new
                {
                    ProjectId = projectId,
                    KeyId = keyId,
                    Label = label,
                    RateLimitPerMinute = rateLimitPerMinute,
                    IsActive = isActive,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>
    /// Changes the fields of a key selected by its id alone. Fields that are
    /// not specified keep their stored value; specifying
    /// <see cref="OptionalField{T}.Value"/> as <c>null</c> clears a nullable
    /// column. Toggling <see cref="ProjectKeyUpdate.IsActive"/> also maintains
    /// <c>revoked_at</c>.
    /// </summary>
    /// <returns>The updated key, or <c>null</c> when it does not exist.</returns>
    public async Task<ProjectKeyRecord?> UpdateKeyAsync(
        long keyId,
        ProjectKeyUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectKeyRow>(new CommandDefinition(
                UpdateKeyByIdSql,
                new
                {
                    KeyId = keyId,
                    HasLabel = update.Label.IsSpecified,
                    Label = update.Label.Value,
                    HasRateLimit = update.RateLimitPerMinute.IsSpecified,
                    RateLimitPerMinute = update.RateLimitPerMinute.Value,
                    HasIsActive = update.IsActive.IsSpecified,
                    IsActive = update.IsActive.Value,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>
    /// Revokes a key selected by its id alone, keeping its row.
    /// </summary>
    /// <returns>The revoked key, or <c>null</c> when it does not exist.</returns>
    public async Task<ProjectKeyRecord?> RevokeKeyAsync(long keyId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectKeyRow>(new CommandDefinition(
                RevokeKeyByIdSql,
                new { KeyId = keyId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>
    /// Revokes a key: it stays in the table, marked inactive with a revocation
    /// timestamp, so the relay stops accepting it.
    /// </summary>
    /// <returns>The revoked key, or <c>null</c> when it does not exist.</returns>
    public async Task<ProjectKeyRecord?> RevokeKeyAsync(
        long projectId,
        long keyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<ProjectKeyRow>(new CommandDefinition(
                RevokeKeySql,
                new { ProjectId = projectId, KeyId = keyId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>
    /// Returns every project ordered by id with only its active keys, which
    /// are ordered by id as well. The result is the configuration a relay
    /// needs to accept and rate limit incoming events.
    /// </summary>
    public async Task<List<ProjectConfig>> ListConfigsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var projects = await connection
            .QueryAsync<ProjectConfigRow>(new CommandDefinition(
                ListConfigProjectsSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var keys = await connection
            .QueryAsync<ProjectKeyConfigRow>(new CommandDefinition(
                ListActiveKeyConfigsSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var keysByProject = keys
            .GroupBy(static key => key.ProjectId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ProjectKeyConfig>)group
                    .Select(static key => key.ToConfig())
                    .ToList());

        return projects
            .Select(project => project.ToConfig(
                keysByProject.TryGetValue(project.Id, out var projectKeys)
                    ? projectKeys
                    : []))
            .ToList();
    }

    private async Task<CreatedProject?> CreateWithKeyAsync(
        string name,
        string slug,
        CancellationToken cancellationToken)
    {
        var publicKey = _publicKeyGenerator.Generate();

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var project = await connection
                .QuerySingleAsync<ProjectRow>(new CommandDefinition(
                    InsertProjectSql,
                    new { Name = name, Slug = slug },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            var key = await connection
                .QuerySingleAsync<ProjectKeyRow>(new CommandDefinition(
                    InsertKeySql,
                    new
                    {
                        ProjectId = project.Id,
                        PublicKey = publicKey,
                        Label = (string?)null,
                        RateLimitPerMinute = (int?)null,
                    },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new CreatedProject(project.ToRecord(), key.ToRecord());
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                && exception.TableName == "projects")
        {
            // The slug is taken; the project and its key were not written.
            await RollbackAsync(transaction).ConfigureAwait(false);
            return null;
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsPublicKeyCollision(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation
        && exception.TableName == "project_keys";

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; PostgreSQL rolls back when
            // it is closed and the next attempt opens a new one.
        }
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class ProjectRow
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Slug { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        public ProjectRecord ToRecord() => new(Id, Name, Slug, AsUtc(CreatedAt));
    }

    private sealed class ProjectSummaryRow
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Slug { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        public string[] FilterReleases { get; init; } = [];

        public string[] FilterEnvironments { get; init; } = [];

        public string[] FilterMessagePatterns { get; init; } = [];

        public long UnresolvedIssueCount { get; init; }

        public long ActiveKeyCount { get; init; }

        public string[] Platforms { get; init; } = [];

        public ProjectSummary ToSummary() =>
            new(
                Id,
                Name,
                Slug,
                AsUtc(CreatedAt),
                UnresolvedIssueCount,
                ActiveKeyCount,
                Platforms,
                new ProjectRecords(FilterReleases, FilterEnvironments, FilterMessagePatterns));
    }

    private sealed class ProjectKeyRow
    {
        public long Id { get; init; }

        public long ProjectId { get; init; }

        public string PublicKey { get; init; } = string.Empty;

        public string? Label { get; init; }

        public bool IsActive { get; init; }

        public int? RateLimitPerMinute { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? RevokedAt { get; init; }

        public ProjectKeyRecord ToRecord() => new(
            Id,
            ProjectId,
            PublicKey,
            Label,
            IsActive,
            RateLimitPerMinute,
            AsUtc(CreatedAt),
            RevokedAt is { } revokedAt ? AsUtc(revokedAt) : null);
    }

    private sealed class ProjectConfigRow
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Slug { get; init; } = string.Empty;

        public string[] FilterReleases { get; init; } = [];

        public string[] FilterEnvironments { get; init; } = [];

        public string[] FilterMessagePatterns { get; init; } = [];

        public ProjectConfig ToConfig(IReadOnlyList<ProjectKeyConfig> keys) =>
            new(
                Id,
                Name,
                Slug,
                keys,
                new ProjectRecords(FilterReleases, FilterEnvironments, FilterMessagePatterns));
    }

    private sealed class ProjectKeyConfigRow
    {
        public long Id { get; init; }

        public long ProjectId { get; init; }

        public string PublicKey { get; init; } = string.Empty;

        public string? Label { get; init; }

        public int? RateLimitPerMinute { get; init; }

        public ProjectKeyConfig ToConfig() => new(Id, PublicKey, Label, RateLimitPerMinute);
    }
}
