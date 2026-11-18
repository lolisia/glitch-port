import { useEffect, useRef } from 'react';
import type { Issue } from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import { LabelChip, LevelBadge, RelativeTime, StatusBadge } from '../../../ui';
import { PlatformChip } from '../../platform/PlatformChip';

export interface IssueTableProps {
  issues: Issue[];
  selection: ReadonlySet<number>;
  onToggle: (id: number) => void;
  onToggleAll: () => void;
}

export function IssueTable({
  issues,
  selection,
  onToggle,
  onToggleAll,
}: IssueTableProps) {
  const { locale, t } = useI18n();
  const selectAllRef = useRef<HTMLInputElement>(null);
  const selectedCount = issues.filter((issue) => selection.has(issue.id)).length;
  const allSelected = issues.length > 0 && selectedCount === issues.length;
  const someSelected = selectedCount > 0 && !allSelected;

  useEffect(() => {
    if (selectAllRef.current !== null) {
      selectAllRef.current.indeterminate = someSelected;
    }
  });

  return (
    <div className="issues-table-scroll">
      <table className="issues-table">
        <thead>
          <tr>
            <th scope="col" className="issues-select-column">
              <input
                ref={selectAllRef}
                type="checkbox"
                aria-label={t('issues.table.selectAll')}
                checked={allSelected}
                onChange={onToggleAll}
              />
            </th>
            <th scope="col">{t('issues.table.title')}</th>
            <th scope="col">{t('issues.table.culprit')}</th>
            <th scope="col">{t('issues.table.level')}</th>
            <th scope="col">{t('issues.table.platform')}</th>
            <th scope="col">{t('issues.table.labels')}</th>
            <th scope="col">{t('issues.table.events')}</th>
            <th scope="col">{t('issues.table.lastSeen')}</th>
            <th scope="col">{t('issues.table.firstSeen')}</th>
            <th scope="col">{t('issues.table.status')}</th>
          </tr>
        </thead>
        <tbody>
          {issues.map((issue) => {
            const selected = selection.has(issue.id);

            return (
              <tr
                key={issue.id}
                className={selected ? 'issue-row issue-row-selected' : 'issue-row'}
              >
                <td className="issues-select-column">
                  <input
                    type="checkbox"
                    aria-label={t('issues.table.selectIssue', { id: issue.id })}
                    checked={selected}
                    onChange={() => onToggle(issue.id)}
                  />
                </td>
                <td className="issues-title">
                  <a href={`#/issues/${issue.id}`}>{issue.title}</a>
                </td>
                <td className="issues-culprit">{issue.culprit ?? ''}</td>
                <td>
                  <LevelBadge level={issue.level} />
                </td>
                <td>
                  <PlatformChip platform={issue.platform} />
                </td>
                <td>
                  <div className="issues-labels">
                    {issue.labels.map((label) => (
                      <LabelChip key={label.id} label={label} />
                    ))}
                  </div>
                </td>
                <td className="issues-number">
                  {formatNumber(issue.event_count, locale)}
                </td>
                <td>
                  <RelativeTime value={issue.last_seen} />
                </td>
                <td>
                  <RelativeTime value={issue.first_seen} />
                </td>
                <td>
                  <StatusBadge status={issue.status} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
