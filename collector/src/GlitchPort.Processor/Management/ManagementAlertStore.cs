using Dapper;
using GlitchPort.Processor.Projects;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>Normalized values of a new alert rule.</summary>
internal sealed record AlertRuleCreate(
    string Name,
    string Trigger,
    string? MinLevel,
    string? Environment,
    int? Threshold,
    int? WindowMinutes,
    int CooldownMinutes,
    string Channel,
    string TargetUrl,
    bool IsEnabled);

/// <summary>
/// The fields of an alert rule that a partial update may change. Omitted
/// fields keep their stored value; an explicitly specified <c>null</c> clears
/// a nullable column.
/// </summary>
internal sealed record AlertRuleUpdate(
    OptionalField<string> Name,
    OptionalField<string> Trigger,
    OptionalField<string?> MinLevel,
    OptionalField<string?> Environment,
    OptionalField<int?> Threshold,
    OptionalField<int?> WindowMinutes,
    OptionalField<int> CooldownMinutes,
    OptionalField<string> Channel,
    OptionalField<string> TargetUrl,
    OptionalField<bool> IsEnabled);

/// <summary>
/// Reads and writes alert rules and reads the notifications queued for them.
/// </summary>
internal sealed class ManagementAlertStore
{
    private const string SelectRuleColumns = """
        id as "Id",
        project_id as "ProjectId",
        name as "Name",
        trigger as "Trigger",
        min_level as "MinLevel",
        environment as "Environment",
        threshold as "Threshold",
        window_minutes as "WindowMinutes",
        cooldown_minutes as "CooldownMinutes",
        channel as "Channel",
        target_url as "TargetUrl",
        is_enabled as "IsEnabled",
        created_at as "CreatedAt",
        updated_at as "UpdatedAt"
        """;

    private const string ListRulesSql = $"""
        select {SelectRuleColumns}
        from alert_rules
        where project_id = @ProjectId
        order by id
        """;

    private const string GetRuleSql = $"""
        select {SelectRuleColumns}
        from alert_rules
        where id = @RuleId
        """;

    private const string InsertRuleSql = $"""
        insert into alert_rules (
            project_id,
            name,
            trigger,
            min_level,
            environment,
            threshold,
            window_minutes,
            cooldown_minutes,
            channel,
            target_url,
            is_enabled)
        values (
            @ProjectId,
            @Name,
            @Trigger,
            @MinLevel,
            @Environment,
            @Threshold,
            @WindowMinutes,
            @CooldownMinutes,
            @Channel,
            @TargetUrl,
            @IsEnabled)
        returning {SelectRuleColumns}
        """;

    // Every column is updated through its "has" flag so an omitted field keeps
    // its stored value while an explicit null clears it. updated_at always
    // moves, even when the patch only repeats the stored values.
    private const string UpdateRuleSql = $"""
        update alert_rules
        set name = case when @HasName then @Name else name end,
            trigger = case when @HasTrigger then @Trigger else trigger end,
            min_level = case when @HasMinLevel then @MinLevel else min_level end,
            environment = case when @HasEnvironment then @Environment else environment end,
            threshold = case when @HasThreshold then @Threshold else threshold end,
            window_minutes = case when @HasWindowMinutes then @WindowMinutes else window_minutes end,
            cooldown_minutes = case when @HasCooldownMinutes then @CooldownMinutes else cooldown_minutes end,
            channel = case when @HasChannel then @Channel else channel end,
            target_url = case when @HasTargetUrl then @TargetUrl else target_url end,
            is_enabled = case when @HasIsEnabled then @IsEnabled else is_enabled end,
            updated_at = now()
        where id = @RuleId
        returning {SelectRuleColumns}
        """;

    private const string DeleteRuleSql = "delete from alert_rules where id = @RuleId";

    private const string ListNotificationsSql = """
        select n.id as "Id",
               n.rule_id as "RuleId",
               r.name as "RuleName",
               n.issue_id as "IssueId",
               n.trigger as "Trigger",
               n.status as "Status",
               n.attempts as "Attempts",
               n.last_error as "LastError",
               n.created_at as "CreatedAt",
               n.sent_at as "SentAt"
        from alert_notifications n
        join alert_rules r on r.id = n.rule_id
        where n.project_id = @ProjectId
        order by n.created_at desc, n.id desc
        limit @Limit
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementAlertStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>Lists the alert rules of a project ordered by id.</summary>
    public async Task<List<AlertRuleResponse>> ListRulesAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<AlertRuleRow>(new CommandDefinition(
                ListRulesSql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(static row => row.ToResponse()).ToList();
    }

    /// <summary>Returns a rule, or <c>null</c> when it does not exist.</summary>
    public async Task<AlertRuleResponse?> GetRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<AlertRuleRow>(new CommandDefinition(
                GetRuleSql,
                new { RuleId = ruleId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToResponse();
    }

    /// <summary>
    /// Creates a rule for <paramref name="projectId"/>.
    /// </summary>
    /// <returns>The created rule, or <c>null</c> when the project does not exist.</returns>
    public async Task<AlertRuleResponse?> CreateRuleAsync(
        long projectId,
        AlertRuleCreate rule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var row = await connection
                .QuerySingleAsync<AlertRuleRow>(new CommandDefinition(
                    InsertRuleSql,
                    new
                    {
                        ProjectId = projectId,
                        rule.Name,
                        rule.Trigger,
                        rule.MinLevel,
                        rule.Environment,
                        rule.Threshold,
                        rule.WindowMinutes,
                        rule.CooldownMinutes,
                        rule.Channel,
                        rule.TargetUrl,
                        rule.IsEnabled,
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            return row.ToResponse();
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // The project does not exist, or was deleted concurrently.
            return null;
        }
    }

    /// <summary>
    /// Applies a partial update, setting <c>updated_at</c> to <c>now()</c>.
    /// </summary>
    /// <returns>The updated rule, or <c>null</c> when it does not exist.</returns>
    public async Task<AlertRuleResponse?> UpdateRuleAsync(
        long ruleId,
        AlertRuleUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<AlertRuleRow>(new CommandDefinition(
                UpdateRuleSql,
                new
                {
                    RuleId = ruleId,
                    HasName = update.Name.IsSpecified,
                    Name = update.Name.Value,
                    HasTrigger = update.Trigger.IsSpecified,
                    Trigger = update.Trigger.Value,
                    HasMinLevel = update.MinLevel.IsSpecified,
                    MinLevel = update.MinLevel.Value,
                    HasEnvironment = update.Environment.IsSpecified,
                    Environment = update.Environment.Value,
                    HasThreshold = update.Threshold.IsSpecified,
                    Threshold = update.Threshold.Value,
                    HasWindowMinutes = update.WindowMinutes.IsSpecified,
                    WindowMinutes = update.WindowMinutes.Value,
                    HasCooldownMinutes = update.CooldownMinutes.IsSpecified,
                    CooldownMinutes = update.CooldownMinutes.Value,
                    HasChannel = update.Channel.IsSpecified,
                    Channel = update.Channel.Value,
                    HasTargetUrl = update.TargetUrl.IsSpecified,
                    TargetUrl = update.TargetUrl.Value,
                    HasIsEnabled = update.IsEnabled.IsSpecified,
                    IsEnabled = update.IsEnabled.Value,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToResponse();
    }

    /// <summary>Deletes a rule and, by cascade, its queued notifications.</summary>
    /// <returns><c>true</c> when a rule was deleted.</returns>
    public async Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                DeleteRuleSql,
                new { RuleId = ruleId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// Lists the newest notifications of a project together with the name of
    /// their rule.
    /// </summary>
    public async Task<List<AlertNotificationResponse>> ListNotificationsAsync(
        long projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<AlertNotificationRow>(new CommandDefinition(
                ListNotificationsSql,
                new { ProjectId = projectId, Limit = limit },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(static row => row.ToResponse()).ToList();
    }

    private sealed class AlertRuleRow
    {
        public long Id { get; init; }

        public long ProjectId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Trigger { get; init; } = string.Empty;

        public string? MinLevel { get; init; }

        public string? Environment { get; init; }

        public int? Threshold { get; init; }

        public int? WindowMinutes { get; init; }

        public int CooldownMinutes { get; init; }

        public string Channel { get; init; } = string.Empty;

        public string TargetUrl { get; init; } = string.Empty;

        public bool IsEnabled { get; init; }

        // Npgsql reads timestamptz as a UTC DateTime; Dapper cannot convert it
        // to DateTimeOffset, so the conversion happens after mapping.
        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }

        public AlertRuleResponse ToResponse() => new(
            Id,
            ProjectId,
            Name,
            Trigger,
            MinLevel,
            Environment,
            Threshold,
            WindowMinutes,
            CooldownMinutes,
            Channel,
            TargetUrl,
            IsEnabled,
            AsUtc(CreatedAt),
            AsUtc(UpdatedAt));
    }

    private sealed class AlertNotificationRow
    {
        public long Id { get; init; }

        public long RuleId { get; init; }

        public string RuleName { get; init; } = string.Empty;

        public long? IssueId { get; init; }

        public string Trigger { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public int Attempts { get; init; }

        public string? LastError { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? SentAt { get; init; }

        public AlertNotificationResponse ToResponse() => new(
            Id,
            RuleId,
            RuleName,
            IssueId,
            Trigger,
            Status,
            Attempts,
            LastError,
            AsUtc(CreatedAt),
            SentAt is { } sentAt ? AsUtc(sentAt) : null);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
