-- Release-aware resolution and regression tracking.
--
-- first_release is the release the issue was first seen in: the release of
-- its earliest event (ordered like first_seen), falling back to the release
-- the issue was last seen in when that earliest event carries none.

alter table issues
    add column first_release text,
    add column resolved_at timestamptz,
    add column resolved_in_release text,
    add column regressed_at timestamptz,
    add column regression_count integer not null default 0;

update issues i
set first_release = coalesce(
    (select e.release
     from events e
     where e.issue_id = i.id
     order by e.timestamp, e.received_at, e.id
     limit 1),
    i.last_release);

create index ix_events_project_release on events (project_id, release) where release is not null;
create index ix_issues_project_first_release on issues (project_id, first_release);
