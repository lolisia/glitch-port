import type { Label } from '../../../api/types';
import { LabelChip } from '../../../ui';

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
  return (
    <section
      className="issue-labels"
      data-testid="issue-labels"
      aria-labelledby="issue-labels-heading"
    >
      <h3 id="issue-labels-heading">Labels</h3>

      <div className="label-chips" data-testid="issue-label-chips">
        {issueLabels.map((label) => (
          <LabelChip key={label.id} label={label} />
        ))}
      </div>

      <div className="label-options" role="group" aria-label="Project labels">
        {projectLabels.map((label) => (
          <label className="label-option" key={label.id}>
            <input
              type="checkbox"
              aria-label={label.name}
              checked={checkedNames.includes(label.name)}
              onChange={(event) => onToggle(label.name, event.target.checked)}
            />
            <span>{label.name}</span>
          </label>
        ))}
      </div>

      <div className="label-new">
        <input
          type="text"
          aria-label="New label names"
          placeholder="New labels, comma separated"
          value={newNames}
          onChange={(event) => onNewNamesChange(event.target.value)}
        />
        <button type="button" onClick={onSave} disabled={saving}>
          Save labels
        </button>
      </div>
    </section>
  );
}
