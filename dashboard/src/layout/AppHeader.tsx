import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Separator } from '@/components/ui/separator';
import { SidebarTrigger } from '@/components/ui/sidebar';
import type { ProjectListItem } from '../api/types';
import { useI18n } from '../i18n';
import type { AppRoute } from '../router';

export type ProjectsState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; projects: ProjectListItem[] };

interface AppHeaderProps {
  route: AppRoute;
  projectsState: ProjectsState;
  activeProjectId: number | null;
  onSelectProject: (projectId: string) => void;
}

/** The plain-text title of the screen currently shown in the content area. */
function screenTitle(route: AppRoute, t: (key: string) => string): string {
  switch (route.name) {
    case 'releases':
      return t('nav.releases');
    case 'messages':
      return t('nav.messages');
    case 'projects':
    case 'project':
      return t('nav.projects');
    case 'settings':
      return t('nav.settings');
    case 'issues':
    case 'issue':
    case 'unknown':
      return t('nav.issues');
  }
}

/**
 * The inset header above the content: the sidebar trigger, the current screen
 * title and the project picker. Theme and language moved to the settings page.
 */
export function AppHeader({
  route,
  projectsState,
  activeProjectId,
  onSelectProject,
}: AppHeaderProps) {
  const { t } = useI18n();

  const renderProjectPicker = () => {
    if (projectsState.status === 'loading') {
      return (
        <span
          className="text-sm text-muted-foreground"
          data-testid="project-picker-state"
        >
          {t('app.projectsLoading')}
        </span>
      );
    }

    if (projectsState.status === 'error') {
      return (
        <span
          className="text-sm text-muted-foreground"
          data-testid="project-picker-state"
        >
          {t('app.projectsUnavailable')}
        </span>
      );
    }

    if (projectsState.projects.length === 0) {
      return (
        <span
          className="text-sm text-muted-foreground"
          data-testid="project-picker-state"
        >
          {t('app.noProjects')} —{' '}
          <a href="#/projects">{t('app.createOne')}</a>.
        </span>
      );
    }

    return (
      <NativeSelect
        aria-label={t('common.project')}
        value={activeProjectId ?? ''}
        onChange={(event) => onSelectProject(event.target.value)}
        size="sm"
        className="min-w-36"
      >
        {projectsState.projects.map((project) => (
          <NativeSelectOption key={project.id} value={project.id}>
            {project.name}
          </NativeSelectOption>
        ))}
      </NativeSelect>
    );
  };

  return (
    <header className="flex h-14 shrink-0 items-center gap-2 border-b bg-background px-4">
      <SidebarTrigger />
      <Separator orientation="vertical" className="h-4" />
      <span className="truncate text-sm font-medium">
        {screenTitle(route, t)}
      </span>
      <div className="ml-auto flex items-center gap-2">
        {renderProjectPicker()}
      </div>
    </header>
  );
}
