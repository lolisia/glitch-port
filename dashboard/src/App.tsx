import { useCallback, useEffect, useState } from 'react';
import { listProjects } from './api/client';
import { TooltipProvider } from '@/components/ui/tooltip';
import { SidebarInset, SidebarProvider } from '@/components/ui/sidebar';
import { AppHeader, type ProjectsState } from './layout/AppHeader';
import { AppSidebar } from './layout/AppSidebar';
import { IssueDetailPage } from './pages/issues/IssueDetailPage';
import { IssuesPage } from './pages/issues/IssuesPage';
import { MessagesPage } from './pages/messages/MessagesPage';
import { ProjectDetailPage } from './pages/projects/ProjectDetailPage';
import { ProjectsPage } from './pages/projects/ProjectsPage';
import { ReleasesPage } from './pages/releases/ReleasesPage';
import { SettingsPage } from './pages/settings/SettingsPage';
import {
  clearStoredProjectId,
  parseConfiguredProjectId,
  readStoredProjectId,
  resolveActiveProjectId,
  writeStoredProjectId,
} from './project/activeProject';
import { navigate, useHashRoute } from './router';

/**
 * The dashboard shell: the sidebar navigation, the header project selector,
 * the route switch and the active project resolution.
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

  // Unknown routes fall back to the issues page, so the Issues link stays
  // highlighted until the normalising effect replaces the hash.
  const unknownRoute = route.name === 'unknown';

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

  return (
    <TooltipProvider>
      <SidebarProvider>
        <AppSidebar route={route} />
        <SidebarInset>
          <AppHeader
            route={route}
            projectsState={projectsState}
            activeProjectId={activeProjectId}
            onSelectProject={selectProject}
          />
          <div className="mx-auto w-full max-w-[1600px] p-4 md:p-6">
            {route.name === 'messages' && <MessagesPage />}

            {route.name === 'releases' && (
              <ReleasesPage projectId={activeProjectId} />
            )}

            {route.name === 'projects' && (
              <ProjectsPage onProjectsChanged={reloadProjects} />
            )}

            {route.name === 'project' && route.projectId !== null && (
              <ProjectDetailPage
                key={route.projectId}
                projectId={route.projectId}
                onProjectsChanged={reloadProjects}
              />
            )}

            {route.name === 'issue' && route.issueId !== null && (
              <IssueDetailPage
                issueId={route.issueId}
                projectId={activeProjectId}
              />
            )}

            {route.name === 'settings' && <SettingsPage />}

            {(route.name === 'issues' || unknownRoute) && (
              <IssuesPage projectId={activeProjectId} />
            )}
          </div>
        </SidebarInset>
      </SidebarProvider>
    </TooltipProvider>
  );
}
