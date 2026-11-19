import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import {
  Card,
  CardContent,
  CardHeader,
} from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { updateProject } from '../../../api/client';
import type { InboundFilters, ProjectDetail } from '../../../api/types';
import { useI18n } from '../../../i18n';
import { ErrorState } from '../../../ui';

function toText(values: string[]): string {
  return values.join('\n');
}

/** One pattern per line; blank lines and surrounding whitespace are dropped. */
function toList(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line !== '');
}

interface InboundFiltersSectionProps {
  projectId: number;
  filters: InboundFilters;
  /** Receives the project returned by the PATCH so the page stays in sync. */
  onSaved: (project: ProjectDetail) => void;
}

/**
 * The three inbound filter lists of a project.
 *
 * Saving always PATCHes the complete set under `inbound_filters`, so an array
 * that was emptied in the editor is sent as `[]` instead of being omitted.
 * Failures stay inside this section: the alert rules and keys keep rendering.
 */
export function InboundFiltersSection({
  projectId,
  filters,
  onSaved,
}: InboundFiltersSectionProps) {
  const { t } = useI18n();
  const [releases, setReleases] = useState(toText(filters.releases));
  const [environments, setEnvironments] = useState(
    toText(filters.environments),
  );
  const [messagePatterns, setMessagePatterns] = useState(
    toText(filters.message_patterns),
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);

  // A successful save refreshes the project; adopt the stored values so the
  // editor never drifts from what the API accepted.
  useEffect(() => {
    setReleases(toText(filters.releases));
    setEnvironments(toText(filters.environments));
    setMessagePatterns(toText(filters.message_patterns));
  }, [filters.releases, filters.environments, filters.message_patterns]);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (saving) {
      return;
    }

    setSaving(true);
    setError(null);
    setSaved(false);

    try {
      const updated = await updateProject(projectId, {
        inbound_filters: {
          releases: toList(releases),
          environments: toList(environments),
          message_patterns: toList(messagePatterns),
        },
      });

      onSaved(updated);
      setSaved(true);
    } catch (saveError) {
      setError(saveError);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Card
      role="region"
      aria-labelledby="project-inbound-filters-heading"
      data-testid="inbound-filters-section"
    >
      <CardHeader>
        <h3
          id="project-inbound-filters-heading"
          className="text-base leading-none font-semibold"
        >
          {t('projects.detail.filters.heading')}
        </h3>
        <p className="text-sm text-muted-foreground">
          {t('projects.detail.filters.description')}
        </p>
      </CardHeader>
      <CardContent>
        <form
          className="grid gap-4 md:grid-cols-3"
          onSubmit={(event) => void handleSubmit(event)}
        >
          <div className="flex flex-col gap-2">
            <Label htmlFor="inbound-filters-releases">
              {t('projects.detail.filters.releases')}
            </Label>
            <Textarea
              id="inbound-filters-releases"
              className="font-mono text-xs"
              rows={4}
              value={releases}
              onChange={(event) => {
                setReleases(event.target.value);
                setSaved(false);
              }}
            />
            <span className="text-xs text-muted-foreground">
              {t('projects.detail.filters.releasesHint')}
            </span>
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="inbound-filters-environments">
              {t('projects.detail.filters.environments')}
            </Label>
            <Textarea
              id="inbound-filters-environments"
              className="font-mono text-xs"
              rows={4}
              value={environments}
              onChange={(event) => {
                setEnvironments(event.target.value);
                setSaved(false);
              }}
            />
            <span className="text-xs text-muted-foreground">
              {t('projects.detail.filters.environmentsHint')}
            </span>
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="inbound-filters-message-patterns">
              {t('projects.detail.filters.messagePatterns')}
            </Label>
            <Textarea
              id="inbound-filters-message-patterns"
              className="font-mono text-xs"
              rows={4}
              value={messagePatterns}
              onChange={(event) => {
                setMessagePatterns(event.target.value);
                setSaved(false);
              }}
            />
            <span className="text-xs text-muted-foreground">
              {t('projects.detail.filters.messagePatternsHint')}
            </span>
          </div>

          <div className="flex flex-wrap items-center gap-3 md:col-span-3">
            <Button type="submit" disabled={saving}>
              {t('projects.detail.filters.save')}
            </Button>
            <span className="text-xs text-muted-foreground">
              {t('projects.detail.filters.syntaxHint')}
            </span>
            {saved && (
              <span className="text-sm font-medium text-success" role="status">
                {t('projects.detail.filters.saved')}
              </span>
            )}
          </div>

          {error !== null && (
            <div className="md:col-span-3">
              <ErrorState
                error={error}
                prefix={t('projects.detail.filters.error')}
              />
            </div>
          )}
        </form>
      </CardContent>
    </Card>
  );
}
