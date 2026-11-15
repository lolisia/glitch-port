import { Fragment, useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import {
  ApiError,
  createProjectKey,
  deleteProject,
  getProject,
  listProjectKeys,
  revokeProjectKey,
  updateProject,
  updateProjectKey,
} from '../../api/client';
import type {
  CreateProjectKeyRequest,
  ProjectDetail,
  ProjectKey,
  UpdateProjectRequest,
} from '../../api/types';
import { navigate } from '../../router';
import { ErrorState, LoadingState, formatAbsoluteTime } from '../../ui';
import { copyToClipboard } from './copy';
import './project-detail.css';

type Loadable<T> =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; data: T };

type CopyState = 'idle' | 'copied' | 'failed';

interface RateLimitParseResult {
  valid: boolean;
  /** `null` clears the limit; only meaningful when `valid` is true. */
  rate: number | null;
}

/**
 * Parses the value of a rate limit field.
 *
 * A blank field means "no per-key limit" and maps to an explicit `null` in the
 * PATCH body. `0` is a valid, meaningful limit (unlimited), while negative or
 * non-integer input is rejected before any request is sent.
 */
function parseRateLimit(value: string): RateLimitParseResult {
  const trimmed = value.trim();

  if (trimmed === '') {
    return { valid: true, rate: null };
  }

  if (!/^\d+$/.test(trimmed)) {
    return { valid: false, rate: null };
  }

  const rate = Number(trimmed);
  return Number.isSafeInteger(rate)
    ? { valid: true, rate }
    : { valid: false, rate: null };
}

function rateLimitText(key: ProjectKey): string {
  return key.rate_limit_per_minute === null
    ? ''
    : String(key.rate_limit_per_minute);
}

const RATE_LIMIT_ERROR = 'Rate limit must be a non-negative integer.';

interface ProjectDetailPageProps {
  projectId: number;
  /** Lets App reload the project selector after a rename or a deletion. */
  onProjectsChanged: () => void;
}

/**
 * `#/projects/<id>`: one project with its edit form, public keys and danger
 * zone.
 *
 * The project detail and its keys load in parallel. A 404 on the project turns
 * the screen into a "not found" state with a link back to the project list,
 * while a key-list failure only disables the key section.
 */
export function ProjectDetailPage({
  projectId,
  onProjectsChanged,
}: ProjectDetailPageProps) {
  const [projectState, setProjectState] = useState<Loadable<ProjectDetail>>({
    status: 'loading',
  });
  const [keysState, setKeysState] = useState<Loadable<ProjectKey[]>>({
    status: 'loading',
  });
  const [createdKey, setCreatedKey] = useState<ProjectKey | null>(null);
  const [createdCopy, setCreatedCopy] = useState<CopyState>('idle');
  const [confirmName, setConfirmName] = useState('');
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<unknown>(null);

  useEffect(() => {
    let cancelled = false;

    setProjectState({ status: 'loading' });
    setKeysState({ status: 'loading' });
    setCreatedKey(null);
    setCreatedCopy('idle');
    setConfirmName('');
    setDeleteError(null);

    // Start both requests together: neither depends on the other, and a key
    // list failure must not hide a project that loaded fine (or vice versa).
    getProject(projectId)
      .then((project) => {
        if (!cancelled) {
          setProjectState({ status: 'ready', data: project });
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setProjectState({ status: 'error', error });
        }
      });

    listProjectKeys(projectId)
      .then((loaded) => {
        if (!cancelled) {
          setKeysState({
            status: 'ready',
            data: Array.isArray(loaded) ? loaded : [],
          });
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setKeysState({ status: 'error', error });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [projectId]);

  /**
   * Reloads the keys after a mutation and keeps the active-key count in the
   * header in sync. Failures are shown in the key section instead of being
   * treated as a failed mutation.
   */
  const refreshKeys = useCallback(async () => {
    try {
      const loaded = await listProjectKeys(projectId);
      const keys = Array.isArray(loaded) ? loaded : [];

      setKeysState({ status: 'ready', data: keys });
      setProjectState((previous) =>
        previous.status === 'ready'
          ? {
              status: 'ready',
              data: {
                ...previous.data,
                key_count: keys.filter((key) => key.is_active).length,
              },
            }
          : previous,
      );
    } catch (error) {
      setKeysState({ status: 'error', error });
    }
  }, [projectId]);

  const handleProjectSaved = (updated: ProjectDetail) => {
    setProjectState({ status: 'ready', data: updated });
    onProjectsChanged();
  };

  const handleKeyUpdated = (updated: ProjectKey) => {
    setKeysState((previous) =>
      previous.status === 'ready'
        ? {
            status: 'ready',
            data: previous.data.map((key) =>
              key.id === updated.id ? updated : key,
            ),
          }
        : previous,
    );
  };

  const handleKeyCreated = async (created: ProjectKey) => {
    setCreatedKey(created);
    setCreatedCopy('idle');
    await refreshKeys();
  };

  const handleKeyRevoked = async (keyId: number) => {
    await revokeProjectKey(keyId);
    await refreshKeys();
  };

  const handleCreatedCopy = async () => {
    if (createdKey === null) {
      return;
    }

    setCreatedCopy((await copyToClipboard(createdKey.dsn)) ? 'copied' : 'failed');
  };

  const handleDelete = async (project: ProjectDetail) => {
    if (deleting || confirmName !== project.name) {
      return;
    }

    setDeleting(true);
    setDeleteError(null);

    try {
      await deleteProject(project.id);
      onProjectsChanged();
      navigate('/projects');
    } catch (error) {
      setDeleteError(error);
      setDeleting(false);
    }
  };

  if (projectState.status === 'loading') {
    return (
      <section className="project-detail-page" data-testid="project-detail-page">
        <LoadingState />
      </section>
    );
  }

  if (
    projectState.status === 'error' &&
    projectState.error instanceof ApiError &&
    projectState.error.status === 404
  ) {
    return (
      <section className="project-detail-page" data-testid="project-detail-page">
        <header className="page-header">
          <h2>Project not found</h2>
        </header>
        <p>
          Project {projectId} does not exist or was deleted.{' '}
          <a href="#/projects">Back to projects</a>
        </p>
      </section>
    );
  }

  if (projectState.status === 'error') {
    return (
      <section className="project-detail-page" data-testid="project-detail-page">
        <header className="page-header">
          <h2>Project {projectId}</h2>
          <a href="#/projects">Back to projects</a>
        </header>
        <ErrorState
          error={projectState.error}
          prefix="Failed to load project"
        />
      </section>
    );
  }

  const project = projectState.data;

  return (
    <section className="project-detail-page" data-testid="project-detail-page">
      <header className="page-header">
        <h2>{project.name}</h2>
        <a href="#/projects">Back to projects</a>
      </header>

      <dl className="project-detail-facts" data-testid="project-detail-facts">
        <div className="project-detail-fact">
          <dt>Slug</dt>
          <dd className="project-detail-mono">{project.slug}</dd>
        </div>
        <div className="project-detail-fact">
          <dt>Platform</dt>
          <dd>{project.platform ?? 'Not set'}</dd>
        </div>
        <div className="project-detail-fact">
          <dt>Created</dt>
          <dd>
            <time dateTime={project.created_at}>
              {formatAbsoluteTime(project.created_at)}
            </time>
          </dd>
        </div>
        <div className="project-detail-fact">
          <dt>Unresolved issues</dt>
          <dd className="project-detail-number">{project.issue_count}</dd>
        </div>
        <div className="project-detail-fact">
          <dt>Active keys</dt>
          <dd className="project-detail-number">{project.key_count}</dd>
        </div>
      </dl>

      <ProjectEditForm
        key={project.id}
        project={project}
        onSaved={handleProjectSaved}
      />

      <section
        className="project-detail-section"
        aria-labelledby="project-keys-heading"
      >
        <h3 id="project-keys-heading">Keys</h3>

        {keysState.status === 'loading' && <LoadingState />}

        {keysState.status === 'error' && (
          <ErrorState error={keysState.error} prefix="Failed to load keys" />
        )}

        {keysState.status === 'ready' && keysState.data.length === 0 && (
          <p className="state state-empty">No keys yet.</p>
        )}

        {keysState.status === 'ready' && keysState.data.length > 0 && (
          <>
            <table className="project-detail-keys">
              <thead>
                <tr>
                  <th scope="col">Public key</th>
                  <th scope="col">Label</th>
                  <th scope="col">Status</th>
                  <th scope="col">Rate limit per minute</th>
                  <th scope="col">DSN</th>
                  <th scope="col">Actions</th>
                </tr>
              </thead>
              <tbody>
                {keysState.data.map((key) => (
                  <KeyRow
                    key={key.id}
                    projectKey={key}
                    onUpdated={handleKeyUpdated}
                    onRevoke={handleKeyRevoked}
                  />
                ))}
              </tbody>
            </table>
            <p className="project-detail-hint">
              A blank rate limit means no per-key limit; 0 means unlimited.
            </p>
          </>
        )}
      </section>

      <CreateKeyForm projectId={projectId} onCreated={handleKeyCreated} />

      {createdKey !== null && (
        <div className="project-detail-created" data-testid="created-key">
          <p>
            Created key{' '}
            <strong>{createdKey.label ?? createdKey.public_key}</strong>. Copy
            its DSN now: the key list always shows it, but this panel is the
            quickest way to hand it to a game build.
          </p>

          <div className="project-detail-created-row">
            <label htmlFor="created-key-dsn">DSN</label>
            <input
              id="created-key-dsn"
              className="project-detail-dsn-input"
              readOnly
              value={createdKey.dsn}
              onFocus={(event) => event.currentTarget.select()}
            />
            <button type="button" onClick={() => void handleCreatedCopy()}>
              Copy new key DSN
            </button>

            {createdCopy === 'copied' && (
              <span className="project-detail-copied" role="status">
                Copied
              </span>
            )}
            {createdCopy === 'failed' && (
              <span className="project-detail-copy-failed" role="status">
                Copy failed
              </span>
            )}
          </div>
        </div>
      )}

      <section
        className="project-detail-danger"
        aria-labelledby="project-danger-heading"
      >
        <h3 id="project-danger-heading">Danger zone</h3>
        <p>
          Deleting <strong>{project.name}</strong> removes its issues, events,
          labels and keys permanently. Type the exact project name to enable the
          button.
        </p>

        <div className="project-detail-field">
          <label htmlFor="project-delete-confirmation">
            Confirm project name
          </label>
          <input
            id="project-delete-confirmation"
            autoComplete="off"
            value={confirmName}
            onChange={(event) => setConfirmName(event.target.value)}
          />
        </div>

        <button
          type="button"
          className="project-detail-delete"
          disabled={confirmName !== project.name || deleting}
          onClick={() => void handleDelete(project)}
        >
          Delete project
        </button>

        {deleteError !== null && (
          <ErrorState error={deleteError} prefix="Failed to delete project" />
        )}
      </section>
    </section>
  );
}

function ProjectEditForm({
  project,
  onSaved,
}: {
  project: ProjectDetail;
  onSaved: (project: ProjectDetail) => void;
}) {
  const [name, setName] = useState(project.name);
  const [platform, setPlatform] = useState(project.platform ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState('');

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    const trimmedName = name.trim();
    if (trimmedName === '' || saving) {
      return;
    }

    const trimmedPlatform = platform.trim();
    const patch: UpdateProjectRequest = {};

    if (trimmedName !== project.name) {
      patch.name = trimmedName;
    }
    if (trimmedPlatform !== (project.platform ?? '')) {
      patch.platform = trimmedPlatform;
    }

    if (Object.keys(patch).length === 0) {
      setError(null);
      setMessage('No changes to save.');
      return;
    }

    setSaving(true);
    setError(null);
    setMessage('');

    try {
      const updated = await updateProject(project.id, patch);
      setName(updated.name);
      setPlatform(updated.platform ?? '');
      onSaved(updated);
      setMessage('Saved');
    } catch (saveError) {
      setError(saveError);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <form
        className="project-detail-form"
        onSubmit={(event) => void handleSubmit(event)}
      >
        <h3>Edit project</h3>

        <div className="project-detail-field">
          <label htmlFor="project-detail-name">Name</label>
          <input
            id="project-detail-name"
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <div className="project-detail-field">
          <label htmlFor="project-detail-platform">Platform</label>
          <input
            id="project-detail-platform"
            value={platform}
            onChange={(event) => setPlatform(event.target.value)}
          />
        </div>

        <button
          type="submit"
          disabled={saving || name.trim() === ''}
        >
          Save changes
        </button>

        {message !== '' && (
          <span className="project-detail-saved" role="status">
            {message}
          </span>
        )}
      </form>

      {error !== null && (
        <ErrorState error={error} prefix="Failed to update project" />
      )}
    </>
  );
}

function KeyRow({
  projectKey,
  onUpdated,
  onRevoke,
}: {
  projectKey: ProjectKey;
  onUpdated: (key: ProjectKey) => void;
  onRevoke: (keyId: number) => Promise<void>;
}) {
  const [rate, setRate] = useState(rateLimitText(projectKey));
  const [savingRate, setSavingRate] = useState(false);
  const [rateError, setRateError] = useState<unknown>(null);
  const [rateSaved, setRateSaved] = useState(false);
  const [revoking, setRevoking] = useState(false);
  const [revokeError, setRevokeError] = useState<unknown>(null);
  const [copyState, setCopyState] = useState<CopyState>('idle');

  useEffect(() => {
    setRate(rateLimitText(projectKey));
  }, [projectKey.rate_limit_per_minute]);

  const handleRateSave = async () => {
    const parsed = parseRateLimit(rate);

    if (!parsed.valid) {
      setRateError(new Error(RATE_LIMIT_ERROR));
      setRateSaved(false);
      return;
    }

    setSavingRate(true);
    setRateError(null);
    setRateSaved(false);

    try {
      const updated = await updateProjectKey(projectKey.id, {
        rate_limit_per_minute: parsed.rate,
      });
      onUpdated(updated);
      setRateSaved(true);
    } catch (error) {
      setRateError(error);
    } finally {
      setSavingRate(false);
    }
  };

  const handleRevoke = async () => {
    setRevoking(true);
    setRevokeError(null);

    try {
      await onRevoke(projectKey.id);
    } catch (error) {
      setRevokeError(error);
    } finally {
      setRevoking(false);
    }
  };

  const handleCopy = async () => {
    setCopyState((await copyToClipboard(projectKey.dsn)) ? 'copied' : 'failed');
  };

  return (
    <Fragment>
      <tr>
        <td>
          <code className="project-detail-key">{projectKey.public_key}</code>
        </td>
        <td>
          {projectKey.label ?? (
            <span className="project-detail-muted">No label</span>
          )}
        </td>
        <td>
          {projectKey.is_active ? (
            <span className="project-key-status project-key-status-active">
              Active
            </span>
          ) : (
            <span className="project-key-status project-key-status-revoked">
              Revoked
              {projectKey.revoked_at !== null && (
                <>
                  {' '}
                  <time dateTime={projectKey.revoked_at}>
                    {formatAbsoluteTime(projectKey.revoked_at)}
                  </time>
                </>
              )}
            </span>
          )}
        </td>
        <td>
          <div className="project-detail-rate">
            <input
              aria-label={`Rate limit for key ${projectKey.id}`}
              inputMode="numeric"
              placeholder="No limit"
              value={rate}
              onChange={(event) => {
                setRate(event.target.value);
                setRateSaved(false);
              }}
            />
            <button
              type="button"
              disabled={savingRate}
              onClick={() => void handleRateSave()}
            >
              Save
            </button>
            {rateSaved && (
              <span className="project-detail-saved" role="status">
                Saved
              </span>
            )}
          </div>
        </td>
        <td>
          <code className="project-detail-dsn">{projectKey.dsn}</code>
          <div className="project-detail-dsn-actions">
            <button type="button" onClick={() => void handleCopy()}>
              Copy DSN
            </button>
            {copyState === 'copied' && (
              <span className="project-detail-copied" role="status">
                Copied
              </span>
            )}
            {copyState === 'failed' && (
              <span className="project-detail-copy-failed" role="status">
                Copy failed
              </span>
            )}
          </div>
        </td>
        <td>
          {projectKey.is_active && (
            <button
              type="button"
              disabled={revoking}
              onClick={() => void handleRevoke()}
            >
              Revoke
            </button>
          )}
        </td>
      </tr>

      {rateError !== null && (
        <tr>
          <td colSpan={6}>
            <ErrorState error={rateError} prefix="Failed to save rate limit" />
          </td>
        </tr>
      )}

      {revokeError !== null && (
        <tr>
          <td colSpan={6}>
            <ErrorState error={revokeError} prefix="Failed to revoke key" />
          </td>
        </tr>
      )}
    </Fragment>
  );
}

function CreateKeyForm({
  projectId,
  onCreated,
}: {
  projectId: number;
  onCreated: (key: ProjectKey) => Promise<void>;
}) {
  const [label, setLabel] = useState('');
  const [rate, setRate] = useState('');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (creating) {
      return;
    }

    const body: CreateProjectKeyRequest = {};
    const trimmedLabel = label.trim();

    if (trimmedLabel !== '') {
      body.label = trimmedLabel;
    }

    if (rate.trim() !== '') {
      const parsed = parseRateLimit(rate);

      if (!parsed.valid || parsed.rate === null) {
        setError(new Error(RATE_LIMIT_ERROR));
        return;
      }

      body.rate_limit_per_minute = parsed.rate;
    }

    setCreating(true);
    setError(null);

    try {
      const created = await createProjectKey(projectId, body);
      setLabel('');
      setRate('');
      await onCreated(created);
    } catch (createError) {
      setError(createError);
    } finally {
      setCreating(false);
    }
  };

  return (
    <>
      <form
        className="project-detail-form"
        onSubmit={(event) => void handleSubmit(event)}
      >
        <h3>Create key</h3>

        <div className="project-detail-field">
          <label htmlFor="create-key-label">Label</label>
          <input
            id="create-key-label"
            placeholder="production"
            value={label}
            onChange={(event) => setLabel(event.target.value)}
          />
        </div>

        <div className="project-detail-field">
          <label htmlFor="create-key-rate-limit">Rate limit per minute</label>
          <input
            id="create-key-rate-limit"
            inputMode="numeric"
            placeholder="No limit"
            value={rate}
            onChange={(event) => setRate(event.target.value)}
          />
          <span className="project-detail-hint">
            Leave blank for no per-key limit, 0 for unlimited.
          </span>
        </div>

        <button type="submit" disabled={creating}>
          Create key
        </button>
      </form>

      {error !== null && (
        <ErrorState error={error} prefix="Failed to create key" />
      )}
    </>
  );
}
