import { EmptyState } from '../../../ui';

/**
 * Shown by project-scoped screens while no project is active: before the
 * project list has loaded, when it is empty, or when loading it failed. The
 * screens must not call the API in that state.
 */
export function NoProjectState() {
  return (
    <EmptyState>
      No project selected — <a href="#/projects">choose a project</a> to continue.
    </EmptyState>
  );
}
