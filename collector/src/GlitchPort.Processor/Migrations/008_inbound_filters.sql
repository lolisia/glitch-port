-- Inbound filters of a project. The Relay drops an event before publishing it
-- when its release, environment or message matches one of the stored glob
-- patterns; an empty array means the filter is disabled. Patterns are glob
-- strings where * matches any string and every other character is literal.

alter table projects
    add column filter_releases text[] not null default '{}',
    add column filter_environments text[] not null default '{}',
    add column filter_message_patterns text[] not null default '{}';
