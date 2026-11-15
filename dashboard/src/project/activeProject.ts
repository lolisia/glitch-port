/**
 * Active project selection for the dashboard.
 *
 * The selected project id is persisted in localStorage so a reload keeps the
 * project the user was browsing. `VITE_PROJECT_ID` only provides the default
 * for a fresh browser (or a deployment pinned to a single project).
 */

/** localStorage key that holds the active project id. */
export const STORAGE_KEY = 'gp.projectId';

/**
 * Parses a raw value into a project id. Project ids are positive integers;
 * missing, blank, zero, negative, fractional and non-numeric values are all
 * rejected so a bad configuration never reaches a project-scoped request.
 */
function toPositiveProjectId(
  value: string | null | undefined,
): number | null {
  if (value === null || value === undefined) {
    return null;
  }

  const trimmed = value.trim();
  if (!/^[0-9]+$/.test(trimmed)) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

/** Returns the project id configured through `VITE_PROJECT_ID`, if valid. */
export function parseConfiguredProjectId(
  value: string | undefined,
): number | null {
  return toPositiveProjectId(value);
}

/** Reads the persisted project id, or null when nothing valid is stored. */
export function readStoredProjectId(): number | null {
  try {
    return toPositiveProjectId(window.localStorage.getItem(STORAGE_KEY));
  } catch {
    // Storage can be unavailable (private mode, blocked cookies); the caller
    // then falls back to the configured or first project.
    return null;
  }
}

/** Persists the active project id for the next visit. */
export function writeStoredProjectId(id: number): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, String(id));
  } catch {
    // A failed write must not break navigation; the selection still applies
    // for the current page.
  }
}

/** Forgets the persisted project id. */
export function clearStoredProjectId(): void {
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // See writeStoredProjectId.
  }
}

/**
 * Resolves the active project from the loaded project list.
 *
 * Order: the stored id when it still exists, then a valid configured id, then
 * the first project, then none. Re-running this after every reload is what
 * makes a project deleted elsewhere fall back instead of producing 404s.
 */
export function resolveActiveProjectId(
  projectIds: readonly number[],
  storedId: number | null,
  configuredId: number | null,
): number | null {
  if (storedId !== null && projectIds.includes(storedId)) {
    return storedId;
  }

  if (configuredId !== null && projectIds.includes(configuredId)) {
    return configuredId;
  }

  return projectIds.length > 0 ? projectIds[0] : null;
}
