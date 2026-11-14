-- Issue grouping, structured events and labels. The legacy messages table from
-- migration 001 is intentionally left untouched: it remains readable for the
-- compatibility management endpoint.

create extension if not exists pg_trgm;

create table issues (
    id bigint generated always as identity primary key,
    project_id bigint not null,
    fingerprint text not null,
    title text not null,
    culprit text,
    level text not null,
    platform text not null,
    status text not null default 'unresolved'
        check (status in ('unresolved', 'resolved', 'ignored')),
    first_seen timestamptz not null,
    last_seen timestamptz not null,
    event_count bigint not null default 0,
    last_release text,
    last_environment text,
    unique (project_id, fingerprint)
);

create index ix_issues_project_status_last_seen on issues (project_id, status, last_seen desc);
create index ix_issues_title_trgm on issues using gin (title gin_trgm_ops);

create table events (
    id uuid primary key,
    project_id bigint not null,
    issue_id bigint not null references issues (id) on delete cascade,
    timestamp timestamptz not null,
    received_at timestamptz not null,
    level text not null,
    platform text not null,
    environment text,
    release text,
    user_id text,
    endpoint text not null,
    message text,
    payload jsonb not null
);

create index ix_events_issue_timestamp on events (issue_id, timestamp desc);
create index ix_events_project_received on events (project_id, received_at desc);

create table labels (
    id bigint generated always as identity primary key,
    project_id bigint not null,
    name text not null,
    color text not null default '#888888',
    unique (project_id, name)
);

create table issue_labels (
    issue_id bigint not null references issues (id) on delete cascade,
    label_id bigint not null references labels (id) on delete cascade,
    primary key (issue_id, label_id)
);
