-- Platforms belong to events, not projects: an SDK may report events from
-- several platforms and an issue already carries the platform of its first
-- event. The observed platforms of a project are derived from its issues, so
-- the project-level hint is removed. The index supports the issue list's
-- platform filter and the per-platform statistics.

alter table projects drop column platform;

create index ix_issues_project_platform on issues (project_id, platform);
