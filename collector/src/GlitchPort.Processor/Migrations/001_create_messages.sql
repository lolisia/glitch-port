create table messages (
    id uuid primary key,
    project_id bigint not null,
    received_at timestamptz not null,
    endpoint text not null,
    message text not null,
    client_timestamp timestamptz,
    sdk_name text,
    sdk_version text,
    stored_at timestamptz not null default now()
);

create index ix_messages_received_at on messages (received_at desc);
