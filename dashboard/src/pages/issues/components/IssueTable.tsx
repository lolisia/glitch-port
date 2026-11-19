import type { Issue } from '../../../api/types';
import { Badge } from '../../../components/ui/badge';
import { Checkbox } from '../../../components/ui/checkbox';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '../../../components/ui/table';
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
  const selectedCount = issues.filter((issue) => selection.has(issue.id)).length;
  const allSelected = issues.length > 0 && selectedCount === issues.length;
  const someSelected = selectedCount > 0 && !allSelected;

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead className="w-8 text-center">
            <Checkbox
              aria-label={t('issues.table.selectAll')}
              checked={
                allSelected ? true : someSelected ? 'indeterminate' : false
              }
              onCheckedChange={() => onToggleAll()}
            />
          </TableHead>
          <TableHead>{t('issues.table.title')}</TableHead>
          <TableHead>{t('issues.table.culprit')}</TableHead>
          <TableHead>{t('issues.table.level')}</TableHead>
          <TableHead>{t('issues.table.platform')}</TableHead>
          <TableHead>{t('issues.table.labels')}</TableHead>
          <TableHead className="text-right">
            {t('issues.table.events')}
          </TableHead>
          <TableHead>{t('issues.table.lastSeen')}</TableHead>
          <TableHead>{t('issues.table.firstSeen')}</TableHead>
          <TableHead>{t('issues.table.status')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {issues.map((issue) => {
          const selected = selection.has(issue.id);

          return (
            <TableRow
              key={issue.id}
              className={selected ? 'issue-row-selected bg-muted/50' : undefined}
            >
              <TableCell className="text-center">
                <Checkbox
                  aria-label={t('issues.table.selectIssue', { id: issue.id })}
                  checked={selected}
                  onCheckedChange={() => onToggle(issue.id)}
                />
              </TableCell>
              <TableCell className="min-w-64 whitespace-normal">
                <a
                  href={`#/issues/${issue.id}`}
                  className="font-semibold text-primary underline-offset-4 hover:underline"
                >
                  {issue.title}
                </a>
                {issue.status === 'unresolved' && issue.regressed_at !== null && (
                  <Badge
                    variant="outline"
                    className="regressed-badge ml-2 border-transparent bg-warning/15 text-warning"
                  >
                    {t('issues.table.regressed')}
                  </Badge>
                )}
              </TableCell>
              <TableCell className="min-w-48 whitespace-normal text-sm text-muted-foreground">
                {issue.culprit ?? ''}
              </TableCell>
              <TableCell>
                <LevelBadge level={issue.level} />
              </TableCell>
              <TableCell>
                <PlatformChip platform={issue.platform} />
              </TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {issue.labels.map((label) => (
                    <LabelChip key={label.id} label={label} />
                  ))}
                </div>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {formatNumber(issue.event_count, locale)}
              </TableCell>
              <TableCell>
                <RelativeTime value={issue.last_seen} />
              </TableCell>
              <TableCell>
                <RelativeTime value={issue.first_seen} />
              </TableCell>
              <TableCell>
                <StatusBadge status={issue.status} />
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}
