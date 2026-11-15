-- Background maintenance bookkeeping (retention runs) and the index that
-- makes the retention scan over events.received_at cheap.

create table maintenance_runs (
    id bigint generated always as identity primary key,
    kind text not null,
    started_at timestamptz not null,
    finished_at timestamptz not null,
    days integer not null,
    deleted_events bigint not null default 0,
    deleted_issues bigint not null default 0,
    deleted_messages bigint not null default 0
);

create index ix_maintenance_runs_kind_finished_at on maintenance_runs (kind, finished_at desc);

create index ix_events_received_at on events (received_at);
