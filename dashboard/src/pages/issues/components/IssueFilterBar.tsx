import { MultiSelectCombobox } from '../../../components/MultiSelectCombobox';
import { Input } from '../../../components/ui/input';
import {
  NativeSelect,
  NativeSelectOption,
} from '../../../components/ui/native-select';
import { Toggle } from '../../../components/ui/toggle';
import {
  ToggleGroup,
  ToggleGroupItem,
} from '../../../components/ui/toggle-group';
import { useI18n } from '../../../i18n';
import {
  LEVEL_OPTIONS,
  SORT_OPTIONS,
  STATUS_TABS,
  parseIssueSortFilter,
  parseIssueStatusFilter,
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
 * Status tabs, the debounced search box and the filter controls. The values are
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
  const { t } = useI18n();

  // Keep platforms restored from a shared link selectable even when the
  // project list has not caught up yet.
  const platformChoices = [...platformOptions];
  for (const platform of filters.platform) {
    if (!platformChoices.includes(platform)) {
      platformChoices.push(platform);
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <ToggleGroup
        type="single"
        value={filters.status}
        // Pressing the active tab is a no-op instead of clearing the status.
        onValueChange={(value) => {
          if (value !== '') {
            onFilterChange({ status: parseIssueStatusFilter(value) });
          }
        }}
        role="group"
        aria-label={t('issues.filters.status')}
        className="flex flex-wrap justify-start gap-1"
      >
        {STATUS_TABS.map((tab) => (
          <ToggleGroupItem
            key={tab.value}
            value={tab.value}
            /* The tab strip behaves as a group of toggle buttons, not radios. */
            role="button"
            aria-checked={undefined}
            aria-pressed={filters.status === tab.value}
            variant="outline"
            size="sm"
            className="data-[state=on]:border-primary data-[state=on]:bg-secondary data-[state=on]:font-semibold"
          >
            {t(tab.labelKey)}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="search"
          aria-label={t('issues.filters.search')}
          placeholder={t('issues.filters.searchPlaceholder')}
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
          className="min-w-56 flex-1 basis-56"
        />

        <NativeSelect
          aria-label={t('issues.filters.level')}
          value={filters.level}
          onChange={(event) => onFilterChange({ level: event.target.value })}
        >
          {LEVEL_OPTIONS.map((option) => (
            <NativeSelectOption key={option.value} value={option.value}>
              {t(option.labelKey)}
            </NativeSelectOption>
          ))}
        </NativeSelect>

        <NativeSelect
          aria-label={t('issues.filters.environment')}
          value={filters.environment}
          onChange={(event) =>
            onFilterChange({ environment: event.target.value })
          }
        >
          <NativeSelectOption value="">{t('issues.filters.allEnvironments')}</NativeSelectOption>
          {environmentOptions.map((value) => (
            <NativeSelectOption key={value} value={value}>
              {value}
            </NativeSelectOption>
          ))}
        </NativeSelect>

        <NativeSelect
          aria-label={t('issues.filters.release')}
          value={filters.release}
          onChange={(event) => onFilterChange({ release: event.target.value })}
        >
          <NativeSelectOption value="">{t('issues.filters.allReleases')}</NativeSelectOption>
          {releaseOptions.map((value) => (
            <NativeSelectOption key={value} value={value}>
              {value}
            </NativeSelectOption>
          ))}
        </NativeSelect>

        <Input
          type="search"
          aria-label={t('issues.filters.firstRelease')}
          placeholder={t('issues.filters.firstReleasePlaceholder')}
          value={filters.first_release}
          onChange={(event) =>
            onFilterChange({ first_release: event.target.value })
          }
          className="min-w-48 flex-1 basis-48"
        />

        <NativeSelect
          aria-label={t('issues.filters.label')}
          value={filters.label}
          onChange={(event) => onFilterChange({ label: event.target.value })}
        >
          <NativeSelectOption value="">{t('issues.filters.allLabels')}</NativeSelectOption>
          {labelNames.map((name) => (
            <NativeSelectOption key={name} value={name}>
              {name}
            </NativeSelectOption>
          ))}
        </NativeSelect>

        {platformChoices.length > 0 && (
          <MultiSelectCombobox
            options={platformChoices}
            value={filters.platform}
            onChange={(platform) => onFilterChange({ platform })}
            ariaLabel={t('issues.filters.platform')}
            placeholder={t('issues.filters.allPlatforms')}
            searchPlaceholder={t('issues.filters.platformSearch')}
            emptyText={t('issues.filters.platformEmpty')}
            clearLabel={t('issues.filters.platformClear')}
            selectedCountLabel={(count) =>
              t('issues.filters.platformCount', { count })
            }
            className="w-56"
          />
        )}

        <NativeSelect
          aria-label={t('issues.filters.sort')}
          value={filters.sort}
          onChange={(event) =>
            onFilterChange({ sort: parseIssueSortFilter(event.target.value) })
          }
        >
          {SORT_OPTIONS.map((option) => (
            <NativeSelectOption key={option.value} value={option.value}>
              {t(option.labelKey)}
            </NativeSelectOption>
          ))}
        </NativeSelect>

        <Toggle
          pressed={filters.regressed}
          onPressedChange={(pressed) => onFilterChange({ regressed: pressed })}
          aria-label={t('issues.filters.regressed')}
          variant="outline"
          size="sm"
          className="border-warning/60 data-[state=on]:border-warning data-[state=on]:bg-warning/15 data-[state=on]:text-warning"
        >
          {t('issues.filters.regressed')}
        </Toggle>
      </div>
    </div>
  );
}
