import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { createProject, listProjects } from '../../api/client';
import type { CreatedProject, ProjectListItem } from '../../api/types';
import { formatNumber, useI18n } from '../../i18n';
import { EmptyState, ErrorState, LoadingState } from '../../ui';
import { PlatformChips } from '../platform/PlatformChip';
import { copyToClipboard } from './copy';
import './projects.css';

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
    <section className="projects-page" data-testid="projects-page">
      <header className="page-header">
        <h2>{t('projects.title')}</h2>
        <button type="button" onClick={() => void load()} disabled={loading}>
          {t('common.refresh')}
        </button>
      </header>

      {loading && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState error={state.error} prefix={t('projects.loadError')} />
      )}

      {state.status === 'ready' && state.projects.length === 0 && (
        <EmptyState>{t('projects.empty')}</EmptyState>
      )}

      {state.status === 'ready' && state.projects.length > 0 && (
        <table className="projects-table">
          <thead>
            <tr>
              <th scope="col">{t('projects.table.name')}</th>
              <th scope="col">{t('projects.table.slug')}</th>
              <th scope="col">{t('projects.table.platforms')}</th>
              <th scope="col">{t('projects.table.unresolvedIssues')}</th>
              <th scope="col">{t('projects.table.activeKeys')}</th>
            </tr>
          </thead>
          <tbody>
            {state.projects.map((project) => (
              <tr key={project.id}>
                <td>
                  <a href={`#/projects/${project.id}`}>{project.name}</a>
                </td>
                <td className="projects-slug">{project.slug}</td>
                <td>
                  <PlatformChips platforms={project.platforms} />
                </td>
                <td className="projects-number">
                  {formatNumber(project.issue_count, locale)}
                </td>
                <td className="projects-number">
                  {formatNumber(project.key_count, locale)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form
        className="project-form"
        onSubmit={(event) => void handleSubmit(event)}
      >
        <h2>{t('projects.create.heading')}</h2>

        <div className="project-field">
          <label htmlFor="project-name">{t('projects.create.name')}</label>
          <input
            id="project-name"
            name="name"
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <button type="submit" disabled={creating || name.trim() === ''}>
          {t('projects.create.submit')}
        </button>
      </form>

      {createError !== null && (
        <ErrorState error={createError} prefix={t('projects.create.error')} />
      )}

      {created !== null && (
        <div className="project-created" data-testid="created-project">
          <p>
            {t('projects.create.created', {
              name: created.name,
              slug: created.slug,
            })}
          </p>

          <label htmlFor="created-project-dsn">{t('projects.dsn')}</label>
          <input
            id="created-project-dsn"
            className="project-dsn"
            readOnly
            value={created.dsn}
            onFocus={(event) => event.currentTarget.select()}
          />

          <button type="button" onClick={() => void handleCopy()}>
            {t('projects.copyDsn')}
          </button>

          {copyState === 'copied' && (
            <span className="project-copied" role="status">
              {t('projects.copied')}
            </span>
          )}
          {copyState === 'failed' && (
            <span className="project-copy-failed" role="status">
              {t('projects.copyFailed')}
            </span>
          )}

          <a href={`#/projects/${created.id}`}>{t('projects.open')}</a>
        </div>
      )}
    </section>
  );
}
