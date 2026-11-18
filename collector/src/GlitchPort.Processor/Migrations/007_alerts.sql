-- Alert rules and the notifications queued for them.
--
-- A rule matches issue changes (new_issue, regression) or an event frequency
-- window. event_frequency rules carry a threshold and a window; every other
-- trigger requires both to be null, which the table check constraint below
-- enforces in addition to the management API validation.
--
-- Notifications are queued in the transaction that stores the events that
-- caused them, deduplicated by dedupe_key (the unique constraint doubles as
-- the conflict target of the queueing insert). The dispatcher claims due rows
-- with "for update skip locked", so several Processor instances never send
-- the same notification twice.

create table alert_rules (
    id bigint generated always as identity primary key,
    project_id bigint not null references projects (id) on delete cascade,
    name text not null,
    trigger text not null check (trigger in ('new_issue', 'regression', 'event_frequency')),
    min_level text check (min_level in ('debug', 'info', 'warning', 'error', 'fatal')),
    environment text,
    threshold integer check (threshold >= 1),
    window_minutes integer check (window_minutes between 1 and 1440),
    cooldown_minutes integer not null default 60 check (cooldown_minutes between 1 and 10080),
    channel text not null check (channel in ('webhook', 'discord', 'slack')),
    target_url text not null,
    is_enabled boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    check (
        (trigger = 'event_frequency' and threshold is not null and window_minutes is not null)
        or (trigger <> 'event_frequency' and threshold is null and window_minutes is null)
    )
);

create index ix_alert_rules_project on alert_rules (project_id) where is_enabled;

create table alert_notifications (
    id bigint generated always as identity primary key,
    rule_id bigint not null references alert_rules (id) on delete cascade,
    project_id bigint not null references projects (id) on delete cascade,
    issue_id bigint references issues (id) on delete set null,
    trigger text not null,
    dedupe_key text not null unique,
    payload jsonb not null,
    status text not null default 'pending' check (status in ('pending', 'sending', 'sent', 'failed')),
    attempts integer not null default 0,
    next_attempt_at timestamptz not null default now(),
    locked_until timestamptz,
    last_error text,
    created_at timestamptz not null default now(),
    sent_at timestamptz
);

create index ix_alert_notifications_due on alert_notifications (next_attempt_at) where status in ('pending', 'sending');
create index ix_alert_notifications_project_created on alert_notifications (project_id, created_at desc);
