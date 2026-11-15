import { useCallback, useEffect, useState } from 'react';
import { listProjects } from './api/client';
import type { ProjectListItem } from './api/types';
import { IssueDetailPage } from './pages/issues/IssueDetailPage';
import { IssuesPage } from './pages/issues/IssuesPage';
import { MessagesPage } from './pages/messages/MessagesPage';
import { ProjectsPage } from './pages/projects/ProjectsPage';
import {
  clearStoredProjectId,
  parseConfiguredProjectId,
  readStoredProjectId,
  resolveActiveProjectId,
  writeStoredProjectId,
} from './project/activeProject';
import { navigate, useHashRoute } from './router';

type ProjectsState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; projects: ProjectListItem[] };

/**
 * The dashboard shell: the top bar project selector, the route switch and the
 * active project resolution.
 *
 * The project list is loaded once on mount and reloaded when a screen reports
 * a change. Every reload re-resolves the active project (stored id, configured
 * id, first project) so a project deleted elsewhere falls back instead of
 * leaving the issues screens pointed at a 404.
 */
export default function App() {
  const route = useHashRoute();
  const [projectsState, setProjectsState] = useState<ProjectsState>({
    status: 'loading',
  });
  const [activeProjectId, setActiveProjectId] = useState<number | null>(null);
  const [projectsVersion, setProjectsVersion] = useState(0);

  const reloadProjects = useCallback(() => {
    setProjectsVersion((version) => version + 1);
  }, []);

  useEffect(() => {
    let cancelled = false;

    setProjectsState({ status: 'loading' });

    listProjects()
      .then((loaded) => {
        if (cancelled) {
          return;
        }

        const projects = Array.isArray(loaded) ? loaded : [];
        setProjectsState({ status: 'ready', projects });

        const resolved = resolveActiveProjectId(
          projects.map((project) => project.id),
          readStoredProjectId(),
          parseConfiguredProjectId(import.meta.env.VITE_PROJECT_ID),
        );

        setActiveProjectId(resolved);

        if (resolved === null) {
          // An empty project list invalidates any persisted selection; keep it
          // out of storage so a later first project is not shadowed by a
          // stale id.
          clearStoredProjectId();
        } else {
          writeStoredProjectId(resolved);
        }
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        // Without a list there is no valid project, so the project-scoped
        // screens must not send requests.
        setProjectsState({ status: 'error', error });
        setActiveProjectId(null);
      });

    return () => {
      cancelled = true;
    };
  }, [projectsVersion]);

  // Unknown routes fall back to the issues page, so the Issues tab stays
  // highlighted until the normalising effect replaces the hash.
  const unknownRoute = route.name === 'unknown';
  const issuesActive =
    route.name === 'issues' || route.name === 'issue' || unknownRoute;
  const projectsActive = route.name === 'projects' || route.name === 'project';

  useEffect(() => {
    const hash = window.location.hash;

    if (unknownRoute || hash === '' || hash === '#' || hash === '#/') {
      navigate('/issues', undefined, { replace: true });
    }
  }, [route, unknownRoute]);

  const selectProject = (value: string) => {
    const id = Number(value);

    if (!Number.isSafeInteger(id) || id <= 0) {
      return;
    }

    setActiveProjectId(id);
    writeStoredProjectId(id);
  };

  const renderProjectPicker = () => {
    if (projectsState.status === 'loading') {
      return (
        <span
          className="project-picker-state"
          data-testid="project-picker-state"
        >
          Loading projects...
        </span>
      );
    }

    if (projectsState.status === 'error') {
      return (
        <span
          className="project-picker-state"
          data-testid="project-picker-state"
        >
          Projects unavailable.
        </span>
      );
    }

    if (projectsState.projects.length === 0) {
      return (
        <span
          className="project-picker-state"
          data-testid="project-picker-state"
        >
          No projects yet — <a href="#/projects">create one</a>.
        </span>
      );
    }

    return (
      <label className="project-picker">
        <span className="project-picker-label">Project</span>
        <select
          aria-label="Project"
          value={activeProjectId ?? ''}
          onChange={(event) => selectProject(event.target.value)}
        >
          {projectsState.projects.map((project) => (
            <option key={project.id} value={project.id}>
              {project.name}
            </option>
          ))}
        </select>
      </label>
    );
  };

  return (
    <>
      <header className="top-bar">
        <h1>glitch-port</h1>
        <nav className="tabs" aria-label="Sections">
          <a
            className="tab"
            href="#/issues"
            aria-current={issuesActive ? 'page' : undefined}
          >
            Issues
          </a>
          <a
            className="tab"
            href="#/messages"
            aria-current={route.name === 'messages' ? 'page' : undefined}
          >
            Messages
          </a>
          <a
            className="tab"
            href="#/projects"
            aria-current={projectsActive ? 'page' : undefined}
          >
            Projects
          </a>
        </nav>
        {renderProjectPicker()}
      </header>

      <main className="page">
        {route.name === 'messages' && <MessagesPage />}

        {route.name === 'projects' && (
          <ProjectsPage onProjectsChanged={reloadProjects} />
        )}

        {route.name === 'project' && route.projectId !== null && (
          <section
            className="project-placeholder"
            data-testid="project-detail-placeholder"
          >
            <header className="page-header">
              <h2>Project {route.projectId}</h2>
            </header>
            <p>
              Project details are not available yet.{' '}
              <a href="#/projects">Back to projects</a>
            </p>
          </section>
        )}

        {route.name === 'issue' && route.issueId !== null && (
          <IssueDetailPage
            issueId={route.issueId}
            projectId={activeProjectId}
          />
        )}

        {(route.name === 'issues' || unknownRoute) && (
          <IssuesPage projectId={activeProjectId} />
        )}
      </main>
    </>
  );
}
