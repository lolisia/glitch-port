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
  InboundFilters,
  ProjectDetail,
  ProjectKey,
  UpdateProjectRequest,
} from '../../api/types';
import { navigate } from '../../router';
import { formatNumber, useI18n } from '../../i18n';
import { ErrorState, LoadingState, formatAbsoluteTime } from '../../ui';
import { PlatformChips } from '../platform/PlatformChip';
import { AlertRulesSection } from './alerts/AlertRulesSection';
import { copyToClipboard } from './copy';
import { InboundFiltersSection } from './filters/InboundFiltersSection';
import './project-detail.css';

/** Used when a project payload predates the inbound filter field. */
const EMPTY_INBOUND_FILTERS: InboundFilters = {
  releases: [],
  environments: [],
  message_patterns: [],
};

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

/** Marks a client-side rate limit validation failure in the error state. */
const INVALID_RATE = Symbol('invalid-rate');

interface ProjectDetailPageProps {
  projectId: number;
  /** Lets App reload the project selector after a rename or a deletion. */
  onProjectsChanged: () => void;
}

/**
 * `#/projects/<id>`: one project with its edit form, public keys, alert rules,
 * inbound filters and danger zone.
 *
 * The project detail and its keys load in parallel. A 404 on the project turns
 * the screen into a "not found" state with a link back to the project list,
 * while a key-list failure only disables the key section. The alert rules and
 * their notifications load from the alert section itself, so its errors stay
 * inside that section too.
 */
export function ProjectDetailPage({
  projectId,
  onProjectsChanged,
}: ProjectDetailPageProps) {
  const { locale, t } = useI18n();
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

  /**
   * Adopts a project returned by a settings-only PATCH (inbound filters). The
   * selector does not show filters, so there is nothing to reload.
   */
  const handleProjectUpdated = (updated: ProjectDetail) => {
    setProjectState({ status: 'ready', data: updated });
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
          <h2>{t('projects.detail.notFound')}</h2>
        </header>
        <p>
          {t('projects.detail.notFoundBody', { id: projectId })}{' '}
          <a href="#/projects">{t('projects.detail.back')}</a>
        </p>
      </section>
    );
  }

  if (projectState.status === 'error') {
    return (
      <section className="project-detail-page" data-testid="project-detail-page">
        <header className="page-header">
          <h2>{t('projects.detail.title', { id: projectId })}</h2>
          <a href="#/projects">{t('projects.detail.back')}</a>
        </header>
        <ErrorState
          error={projectState.error}
          prefix={t('projects.detail.loadError')}
        />
      </section>
    );
  }

  const project = projectState.data;

  return (
    <section className="project-detail-page" data-testid="project-detail-page">
      <header className="page-header">
        <h2>{project.name}</h2>
        <a href="#/projects">{t('projects.detail.back')}</a>
      </header>

      <dl className="project-detail-facts" data-testid="project-detail-facts">
        <div className="project-detail-fact">
          <dt>{t('projects.detail.slug')}</dt>
          <dd className="project-detail-mono">{project.slug}</dd>
        </div>
        <div className="project-detail-fact">
          <dt>{t('projects.detail.platformsSeen')}</dt>
          <dd>
            <PlatformChips platforms={project.platforms} />
          </dd>
        </div>
        <div className="project-detail-fact">
          <dt>{t('projects.detail.created')}</dt>
          <dd>
            <time dateTime={project.created_at}>
              {formatAbsoluteTime(project.created_at, locale)}
            </time>
          </dd>
        </div>
        <div className="project-detail-fact">
          <dt>{t('projects.detail.unresolvedIssues')}</dt>
          <dd className="project-detail-number">
            {formatNumber(project.issue_count, locale)}
          </dd>
        </div>
        <div className="project-detail-fact">
          <dt>{t('projects.detail.activeKeys')}</dt>
          <dd className="project-detail-number">
            {formatNumber(project.key_count, locale)}
          </dd>
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
        <h3 id="project-keys-heading">{t('projects.detail.keys.heading')}</h3>

        {keysState.status === 'loading' && <LoadingState />}

        {keysState.status === 'error' && (
          <ErrorState
            error={keysState.error}
            prefix={t('projects.detail.keys.loadError')}
          />
        )}

        {keysState.status === 'ready' && keysState.data.length === 0 && (
          <p className="state state-empty">{t('projects.detail.keys.empty')}</p>
        )}

        {keysState.status === 'ready' && keysState.data.length > 0 && (
          <>
            <table className="project-detail-keys">
              <thead>
                <tr>
                  <th scope="col">{t('projects.detail.keys.publicKey')}</th>
                  <th scope="col">{t('projects.detail.keys.label')}</th>
                  <th scope="col">{t('projects.detail.keys.status')}</th>
                  <th scope="col">{t('projects.detail.keys.rateLimit')}</th>
                  <th scope="col">{t('projects.detail.keys.dsn')}</th>
                  <th scope="col">{t('projects.detail.keys.actions')}</th>
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
              {t('projects.detail.keys.hint')}
            </p>
          </>
        )}
      </section>

      <CreateKeyForm projectId={projectId} onCreated={handleKeyCreated} />

      {createdKey !== null && (
        <div className="project-detail-created" data-testid="created-key">
          <p>
            {t('projects.detail.createdKey.message', {
              name: createdKey.label ?? createdKey.public_key,
            })}
          </p>

          <div className="project-detail-created-row">
            <label htmlFor="created-key-dsn">{t('projects.dsn')}</label>
            <input
              id="created-key-dsn"
              className="project-detail-dsn-input"
              readOnly
              value={createdKey.dsn}
              onFocus={(event) => event.currentTarget.select()}
            />
            <button type="button" onClick={() => void handleCreatedCopy()}>
              {t('projects.detail.createdKey.copy')}
            </button>

            {createdCopy === 'copied' && (
              <span className="project-detail-copied" role="status">
                {t('projects.copied')}
              </span>
            )}
            {createdCopy === 'failed' && (
              <span className="project-detail-copy-failed" role="status">
                {t('projects.copyFailed')}
              </span>
            )}
          </div>
        </div>
      )}

      <AlertRulesSection projectId={projectId} />

      <InboundFiltersSection
        projectId={projectId}
        filters={project.inbound_filters ?? EMPTY_INBOUND_FILTERS}
        onSaved={handleProjectUpdated}
      />

      <section
        className="project-detail-danger"
        aria-labelledby="project-danger-heading"
      >
        <h3 id="project-danger-heading">{t('projects.detail.danger.heading')}</h3>
        <p>{t('projects.detail.danger.warning', { name: project.name })}</p>

        <div className="project-detail-field">
          <label htmlFor="project-delete-confirmation">
            {t('projects.detail.danger.confirm')}
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
          {t('projects.detail.danger.submit')}
        </button>

        {deleteError !== null && (
          <ErrorState
            error={deleteError}
            prefix={t('projects.detail.danger.error')}
          />
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
  const { t } = useI18n();
  const [name, setName] = useState(project.name);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState<'saved' | 'noChanges' | null>(null);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    const trimmedName = name.trim();
    if (trimmedName === '' || saving) {
      return;
    }

    const patch: UpdateProjectRequest = {};

    if (trimmedName !== project.name) {
      patch.name = trimmedName;
    }

    if (Object.keys(patch).length === 0) {
      setError(null);
      setMessage('noChanges');
      return;
    }

    setSaving(true);
    setError(null);
    setMessage(null);

    try {
      const updated = await updateProject(project.id, patch);
      setName(updated.name);
      onSaved(updated);
      setMessage('saved');
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
        <h3>{t('projects.detail.edit.heading')}</h3>

        <div className="project-detail-field">
          <label htmlFor="project-detail-name">
            {t('projects.detail.edit.name')}
          </label>
          <input
            id="project-detail-name"
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <button
          type="submit"
          disabled={saving || name.trim() === ''}
        >
          {t('projects.detail.edit.save')}
        </button>

        {message !== null && (
          <span className="project-detail-saved" role="status">
            {t(
              message === 'saved'
                ? 'projects.detail.edit.saved'
                : 'projects.detail.edit.noChanges',
            )}
          </span>
        )}
      </form>

      {error !== null && (
        <ErrorState
          error={error}
          prefix={t('projects.detail.edit.error')}
        />
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
  const { locale, t } = useI18n();
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
      setRateError(INVALID_RATE);
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

  const rateErrorValue =
    rateError === INVALID_RATE
      ? new Error(t('projects.detail.keys.rateInvalid'))
      : rateError;

  return (
    <Fragment>
      <tr>
        <td>
          <code className="project-detail-key">{projectKey.public_key}</code>
        </td>
        <td>
          {projectKey.label ?? (
            <span className="project-detail-muted">
              {t('projects.detail.keys.noLabel')}
            </span>
          )}
        </td>
        <td>
          {projectKey.is_active ? (
            <span className="project-key-status project-key-status-active">
              {t('projects.detail.keys.active')}
            </span>
          ) : (
            <span className="project-key-status project-key-status-revoked">
              {t('projects.detail.keys.revoked')}
              {projectKey.revoked_at !== null && (
                <>
                  {' '}
                  <time dateTime={projectKey.revoked_at}>
                    {formatAbsoluteTime(projectKey.revoked_at, locale)}
                  </time>
                </>
              )}
            </span>
          )}
        </td>
        <td>
          <div className="project-detail-rate">
            <input
              aria-label={t('projects.detail.keys.rateLimitForKey', {
                id: projectKey.id,
              })}
              inputMode="numeric"
              placeholder={t('projects.detail.keys.noLimit')}
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
              {t('projects.detail.keys.save')}
            </button>
            {rateSaved && (
              <span className="project-detail-saved" role="status">
                {t('projects.detail.keys.saved')}
              </span>
            )}
          </div>
        </td>
        <td>
          <code className="project-detail-dsn">{projectKey.dsn}</code>
          <div className="project-detail-dsn-actions">
            <button type="button" onClick={() => void handleCopy()}>
              {t('projects.copyDsn')}
            </button>
            {copyState === 'copied' && (
              <span className="project-detail-copied" role="status">
                {t('projects.copied')}
              </span>
            )}
            {copyState === 'failed' && (
              <span className="project-detail-copy-failed" role="status">
                {t('projects.copyFailed')}
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
              {t('projects.detail.keys.revoke')}
            </button>
          )}
        </td>
      </tr>

      {rateErrorValue !== null && (
        <tr>
          <td colSpan={6}>
            <ErrorState
              error={rateErrorValue}
              prefix={t('projects.detail.keys.rateError')}
            />
          </td>
        </tr>
      )}

      {revokeError !== null && (
        <tr>
          <td colSpan={6}>
            <ErrorState
              error={revokeError}
              prefix={t('projects.detail.keys.revokeError')}
            />
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
  const { t } = useI18n();
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
        setError(INVALID_RATE);
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

  const errorValue =
    error === INVALID_RATE
      ? new Error(t('projects.detail.keys.rateInvalid'))
      : error;

  return (
    <>
      <form
        className="project-detail-form"
        onSubmit={(event) => void handleSubmit(event)}
      >
        <h3>{t('projects.detail.createKey.heading')}</h3>

        <div className="project-detail-field">
          <label htmlFor="create-key-label">
            {t('projects.detail.createKey.label')}
          </label>
          <input
            id="create-key-label"
            placeholder={t('projects.detail.createKey.labelPlaceholder')}
            value={label}
            onChange={(event) => setLabel(event.target.value)}
          />
        </div>

        <div className="project-detail-field">
          <label htmlFor="create-key-rate-limit">
            {t('projects.detail.createKey.rateLimit')}
          </label>
          <input
            id="create-key-rate-limit"
            inputMode="numeric"
            placeholder={t('projects.detail.createKey.rateLimitPlaceholder')}
            value={rate}
            onChange={(event) => setRate(event.target.value)}
          />
          <span className="project-detail-hint">
            {t('projects.detail.createKey.hint')}
          </span>
        </div>

        <button type="submit" disabled={creating}>
          {t('projects.detail.createKey.submit')}
        </button>
      </form>

      {errorValue !== null && (
        <ErrorState
          error={errorValue}
          prefix={t('projects.detail.createKey.error')}
        />
      )}
    </>
  );
}
