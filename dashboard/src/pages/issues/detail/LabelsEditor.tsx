import type { Label } from '../../../api/types';
import { useI18n } from '../../../i18n';
import { LabelChip } from '../../../ui';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label as FieldLabel } from '@/components/ui/label';

interface LabelsEditorProps {
  /** Labels available in the project, one checkbox is rendered per entry. */
  projectLabels: Label[];
  /** Labels currently attached to the issue, rendered as chips. */
  issueLabels: Label[];
  checkedNames: string[];
  newNames: string;
  saving: boolean;
  onToggle: (name: string, checked: boolean) => void;
  onNewNamesChange: (value: string) => void;
  onSave: () => void;
}

export function LabelsEditor({
  projectLabels,
  issueLabels,
  checkedNames,
  newNames,
  saving,
  onToggle,
  onNewNamesChange,
  onSave,
}: LabelsEditorProps) {
  const { t } = useI18n();

  return (
    <Card className="gap-3 p-6" data-testid="issue-labels">
      <h3 id="issue-labels-heading" className="m-0 text-base font-semibold">
        {t('issues.detail.labels.title')}
      </h3>

      <div
        className="flex flex-wrap items-center gap-1.5"
        data-testid="issue-label-chips"
      >
        {issueLabels.length === 0 ? (
          <span className="text-sm text-muted-foreground">
            {t('issues.detail.labels.empty')}
          </span>
        ) : (
          issueLabels.map((label) => <LabelChip key={label.id} label={label} />)
        )}
      </div>

      <div
        className="flex flex-wrap gap-x-4 gap-y-2"
        role="group"
        aria-label={t('issues.detail.labels.projectLabels')}
      >
        {projectLabels.map((label) => {
          const checkboxId = `issue-label-${label.id}`;

          return (
            <div className="flex items-center gap-2" key={label.id}>
              <Checkbox
                id={checkboxId}
                aria-label={label.name}
                checked={checkedNames.includes(label.name)}
                onCheckedChange={(checked) =>
                  onToggle(label.name, checked === true)
                }
              />
              <FieldLabel
                htmlFor={checkboxId}
                className="text-sm font-normal text-foreground"
              >
                {label.name}
              </FieldLabel>
            </div>
          );
        })}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="text"
          aria-label={t('issues.detail.labels.newNames')}
          placeholder={t('issues.detail.labels.newPlaceholder')}
          value={newNames}
          onChange={(event) => onNewNamesChange(event.target.value)}
          className="max-w-sm flex-1"
        />
        <Button type="button" onClick={onSave} disabled={saving}>
          {t('issues.detail.labels.save')}
        </Button>
      </div>
    </Card>
  );
}
