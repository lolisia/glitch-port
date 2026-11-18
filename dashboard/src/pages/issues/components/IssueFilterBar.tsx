import {
  LEVEL_OPTIONS,
  SORT_OPTIONS,
  STATUS_TABS,
  parseIssueSortFilter,
} from './filters';
import type { IssueFilters } from './filters';

export interface IssueFilterBarProps {
  filters: IssueFilters;
  search: string;
  labelNames: string[];
  environmentOptions: string[];
  releaseOptions: string[];
  /** Platforms seen in the active project; the control is hidden when empty. */
  platformOptions: string[];
  onFilterChange: (patch: Partial<IssueFilters>) => void;
  onSearchChange: (value: string) => void;
}

/**
 * Status tabs, the debounced search box and the select filters. The values are
 * owned by the URL query; this component only reports changes.
 */
export function IssueFilterBar({
  filters,
  search,
  labelNames,
  environmentOptions,
  releaseOptions,
  platformOptions,
  onFilterChange,
  onSearchChange,
}: IssueFilterBarProps) {
  // Keep platforms restored from a shared link selectable even when the
  // project list has not caught up yet.
  const platformChoices = [...platformOptions];
  for (const platform of filters.platform) {
    if (!platformChoices.includes(platform)) {
      platformChoices.push(platform);
    }
  }

  return (
    <div className="issues-filters">
      <div className="issues-status-tabs" role="group" aria-label="Status">
        {STATUS_TABS.map((tab) => (
          <button
            key={tab.value}
            type="button"
            aria-pressed={filters.status === tab.value}
            onClick={() => onFilterChange({ status: tab.value })}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="issues-filter-controls">
        <input
          type="search"
          className="issues-search"
          aria-label="Search issues"
          placeholder="Search title..."
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
        />

        <select
          aria-label="Level"
          value={filters.level}
          onChange={(event) => onFilterChange({ level: event.target.value })}
        >
          {LEVEL_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>

        <select
          aria-label="Environment"
          value={filters.environment}
          onChange={(event) =>
            onFilterChange({ environment: event.target.value })
          }
        >
          <option value="">All environments</option>
          {environmentOptions.map((value) => (
            <option key={value} value={value}>
              {value}
            </option>
          ))}
        </select>

        <select
          aria-label="Release"
          value={filters.release}
          onChange={(event) => onFilterChange({ release: event.target.value })}
        >
          <option value="">All releases</option>
          {releaseOptions.map((value) => (
            <option key={value} value={value}>
              {value}
            </option>
          ))}
        </select>

        <select
          aria-label="Label"
          value={filters.label}
          onChange={(event) => onFilterChange({ label: event.target.value })}
        >
          <option value="">All labels</option>
          {labelNames.map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </select>

        {platformChoices.length > 0 && (
          <select
            aria-label="Platform"
            className="issues-platform-filter"
            multiple
            size={Math.min(platformChoices.length, 3)}
            value={filters.platform}
            onChange={(event) =>
              onFilterChange({
                platform: Array.from(
                  event.target.selectedOptions,
                  (option) => option.value,
                ),
              })
            }
          >
            {platformChoices.map((platform) => (
              <option key={platform} value={platform}>
                {platform}
              </option>
            ))}
          </select>
        )}

        <select
          aria-label="Sort"
          value={filters.sort}
          onChange={(event) =>
            onFilterChange({ sort: parseIssueSortFilter(event.target.value) })
          }
        >
          {SORT_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}
