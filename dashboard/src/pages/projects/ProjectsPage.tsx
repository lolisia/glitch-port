import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Copy, RefreshCw } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Card,
  CardContent,
  CardHeader,
} from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { createProject, listProjects } from '../../api/client';
import type { CreatedProject, ProjectListItem } from '../../api/types';
import { formatNumber, useI18n } from '../../i18n';
import { EmptyState, ErrorState, LoadingState } from '../../ui';
import { PlatformChips } from '../platform/PlatformChip';
import { copyToClipboard } from './copy';

type ListState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; projects: ProjectListItem[] };

type CopyState = 'idle' | 'copied' | 'failed';

interface ProjectsPageProps {
  /** Lets App reload its own project list and selector after a create. */
  onProjectsChanged: () => void;
}

/**
 * `#/projects`: the project list and the create form.
 *
 * Creating a project returns the DSN of the key created alongside it, which is
 * the only time the dashboard sees that key: the field is read-only and offers
 * a copy button so it is not lost.
 */
export function ProjectsPage({ onProjectsChanged }: ProjectsPageProps) {
  const { locale, t } = useI18n();
  const [state, setState] = useState<ListState>({ status: 'loading' });
  const [name, setName] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<unknown>(null);
  const [created, setCreated] = useState<CreatedProject | null>(null);
  const [copyState, setCopyState] = useState<CopyState>('idle');

  const load = useCallback(async () => {
    setState({ status: 'loading' });

    try {
      const projects = await listProjects();
      setState({
        status: 'ready',
        projects: Array.isArray(projects) ? projects : [],
      });
    } catch (error) {
      setState({ status: 'error', error });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    const trimmedName = name.trim();
    if (trimmedName === '' || creating) {
      return;
    }

    setCreating(true);
    setCreateError(null);
    setCreated(null);
    setCopyState('idle');

    try {
      const project = await createProject({ name: trimmedName });

      setCreated(project);
      setName('');
      onProjectsChanged();
      void load();
    } catch (error) {
      setCreateError(error);
    } finally {
      setCreating(false);
    }
  };

  const handleCopy = async () => {
    if (created === null) {
      return;
    }

    setCopyState((await copyToClipboard(created.dsn)) ? 'copied' : 'failed');
  };

  const loading = state.status === 'loading';

  return (
    <section className="flex flex-col gap-6" data-testid="projects-page">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold tracking-tight">
          {t('projects.title')}
        </h2>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => void load()}
          disabled={loading}
        >
          <RefreshCw aria-hidden="true" />
          {t('common.refresh')}
        </Button>
      </header>

      {loading && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState error={state.error} prefix={t('projects.loadError')} />
      )}

      {state.status === 'ready' && state.projects.length === 0 && (
        <Card className="border-dashed">
          <CardContent className="py-2">
            <EmptyState>{t('projects.empty')}</EmptyState>
          </CardContent>
        </Card>
      )}

      {state.status === 'ready' && state.projects.length > 0 && (
        <Card className="gap-0 overflow-hidden py-0">
          <Table>
            <TableHeader>
              <TableRow className="bg-muted/50 hover:bg-muted/50">
                <TableHead>{t('projects.table.name')}</TableHead>
                <TableHead>{t('projects.table.slug')}</TableHead>
                <TableHead>{t('projects.table.platforms')}</TableHead>
                <TableHead className="text-right">
                  {t('projects.table.unresolvedIssues')}
                </TableHead>
                <TableHead className="text-right">
                  {t('projects.table.activeKeys')}
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {state.projects.map((project) => (
                <TableRow key={project.id}>
                  <TableCell>
                    <a
                      className="font-medium underline-offset-4 hover:underline"
                      href={`#/projects/${project.id}`}
                    >
                      {project.name}
                    </a>
                  </TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">
                    {project.slug}
                  </TableCell>
                  <TableCell>
                    <PlatformChips platforms={project.platforms} />
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatNumber(project.issue_count, locale)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatNumber(project.key_count, locale)}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      )}

      <Card>
        <CardHeader>
          <h2 className="text-base leading-none font-semibold">
            {t('projects.create.heading')}
          </h2>
        </CardHeader>
        <CardContent>
          <form
            className="flex flex-col gap-4 sm:flex-row sm:items-end"
            onSubmit={(event) => void handleSubmit(event)}
          >
            <div className="flex flex-1 flex-col gap-2">
              <Label htmlFor="project-name">{t('projects.create.name')}</Label>
              <Input
                id="project-name"
                name="name"
                required
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
            </div>

            <Button
              type="submit"
              disabled={creating || name.trim() === ''}
            >
              {t('projects.create.submit')}
            </Button>
          </form>
        </CardContent>
      </Card>

      {createError !== null && (
        <ErrorState error={createError} prefix={t('projects.create.error')} />
      )}

      {created !== null && (
        <Card
          className="border-success/50 bg-success/5"
          data-testid="created-project"
        >
          <CardContent className="flex flex-col gap-4">
            <p className="text-sm">
              {t('projects.create.created', {
                name: created.name,
                slug: created.slug,
              })}
            </p>

            <div className="flex flex-col gap-2">
              <Label htmlFor="created-project-dsn">{t('projects.dsn')}</Label>
              <div className="flex flex-wrap items-center gap-2">
                <Input
                  id="created-project-dsn"
                  className="min-w-0 flex-1 font-mono text-xs"
                  readOnly
                  value={created.dsn}
                  onFocus={(event) => event.currentTarget.select()}
                />
                <Button type="button" variant="outline" onClick={() => void handleCopy()}>
                  <Copy aria-hidden="true" />
                  {t('projects.copyDsn')}
                </Button>

                {copyState === 'copied' && (
                  <span className="text-sm font-medium text-success" role="status">
                    {t('projects.copied')}
                  </span>
                )}
                {copyState === 'failed' && (
                  <span className="text-sm font-medium text-destructive" role="status">
                    {t('projects.copyFailed')}
                  </span>
                )}
              </div>
            </div>

            <a
              className="w-fit text-sm font-medium underline-offset-4 hover:underline"
              href={`#/projects/${created.id}`}
            >
              {t('projects.open')}
            </a>
          </CardContent>
        </Card>
      )}
    </section>
  );
}
