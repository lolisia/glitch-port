import { Fragment, useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Copy } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
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
import { formatNumber, useI18n } from '../../i18n';
import { navigate } from '../../router';
import { ErrorState, LoadingState, formatAbsoluteTime } from '../../ui';
import { PlatformChips } from '../platform/PlatformChip';
import { AlertRulesSection } from './alerts/AlertRulesSection';
import { copyToClipboard } from './copy';
import { InboundFiltersSection } from './filters/InboundFiltersSection';

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
      <section className="flex flex-col gap-6" data-testid="project-detail-page">
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
      <section className="flex flex-col gap-4" data-testid="project-detail-page">
        <h2 className="text-2xl font-semibold tracking-tight">
          {t('projects.detail.notFound')}
        </h2>
        <p className="text-sm text-muted-foreground">
          {t('projects.detail.notFoundBody', { id: projectId })}{' '}
          <a
            className="font-medium underline-offset-4 hover:underline"
            href="#/projects"
          >
            {t('projects.detail.back')}
          </a>
        </p>
      </section>
    );
  }

  if (projectState.status === 'error') {
    return (
      <section className="flex flex-col gap-6" data-testid="project-detail-page">
        <header className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-2xl font-semibold tracking-tight">
            {t('projects.detail.title', { id: projectId })}
          </h2>
          <a
            className="text-sm font-medium text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
            href="#/projects"
          >
            {t('projects.detail.back')}
          </a>
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
    <section className="flex flex-col gap-6" data-testid="project-detail-page">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-2xl font-semibold tracking-tight">
          {project.name}
        </h2>
        <a
          className="text-sm font-medium text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
          href="#/projects"
        >
          {t('projects.detail.back')}
        </a>
      </header>

      <Card data-testid="project-detail-facts">
        <CardContent>
          <dl className="grid grid-cols-2 gap-4 sm:grid-cols-3 xl:grid-cols-5">
            <div className="flex flex-col gap-1">
              <dt className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                {t('projects.detail.slug')}
              </dt>
              <dd className="project-detail-mono font-mono text-sm">
                {project.slug}
              </dd>
            </div>
            <div className="flex flex-col gap-1">
              <dt className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                {t('projects.detail.platformsSeen')}
              </dt>
              <dd>
                <PlatformChips platforms={project.platforms} />
              </dd>
            </div>
            <div className="flex flex-col gap-1">
              <dt className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                {t('projects.detail.created')}
              </dt>
              <dd className="text-sm">
                <time dateTime={project.created_at}>
                  {formatAbsoluteTime(project.created_at, locale)}
                </time>
              </dd>
            </div>
            <div className="flex flex-col gap-1">
              <dt className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                {t('projects.detail.unresolvedIssues')}
              </dt>
              <dd className="text-sm font-semibold tabular-nums">
                {formatNumber(project.issue_count, locale)}
              </dd>
            </div>
            <div className="flex flex-col gap-1">
              <dt className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                {t('projects.detail.activeKeys')}
              </dt>
              <dd className="text-sm font-semibold tabular-nums">
                {formatNumber(project.key_count, locale)}
              </dd>
            </div>
          </dl>
        </CardContent>
      </Card>

      <ProjectEditForm
        key={project.id}
        project={project}
        onSaved={handleProjectSaved}
      />

      <Card role="region" aria-labelledby="project-keys-heading">
        <CardHeader>
          <h3
            id="project-keys-heading"
            className="text-base leading-none font-semibold"
          >
            {t('projects.detail.keys.heading')}
          </h3>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
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
              <div className="overflow-hidden rounded-lg border">
                <Table>
                  <TableHeader>
                    <TableRow className="bg-muted/50 hover:bg-muted/50">
                      <TableHead>{t('projects.detail.keys.publicKey')}</TableHead>
                      <TableHead>{t('projects.detail.keys.label')}</TableHead>
                      <TableHead>{t('projects.detail.keys.status')}</TableHead>
                      <TableHead>{t('projects.detail.keys.rateLimit')}</TableHead>
                      <TableHead>{t('projects.detail.keys.dsn')}</TableHead>
                      <TableHead>{t('projects.detail.keys.actions')}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {keysState.data.map((key) => (
                      <KeyRow
                        key={key.id}
                        projectKey={key}
                        onUpdated={handleKeyUpdated}
                        onRevoke={handleKeyRevoked}
                      />
                    ))}
                  </TableBody>
                </Table>
              </div>
              <p className="text-xs text-muted-foreground">
                {t('projects.detail.keys.hint')}
              </p>
            </>
          )}
        </CardContent>
      </Card>

      <CreateKeyForm projectId={projectId} onCreated={handleKeyCreated} />

      {createdKey !== null && (
        <Card
          className="border-success/50 bg-success/5"
          data-testid="created-key"
        >
          <CardContent className="flex flex-col gap-3">
            <p className="text-sm">
              {t('projects.detail.createdKey.message', {
                name: createdKey.label ?? createdKey.public_key,
              })}
            </p>

            <div className="flex flex-col gap-2">
              <Label htmlFor="created-key-dsn">{t('projects.dsn')}</Label>
              <div className="flex flex-wrap items-center gap-2">
                <Input
                  id="created-key-dsn"
                  className="min-w-0 flex-1 font-mono text-xs"
                  readOnly
                  value={createdKey.dsn}
                  onFocus={(event) => event.currentTarget.select()}
                />
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => void handleCreatedCopy()}
                >
                  <Copy aria-hidden="true" />
                  {t('projects.detail.createdKey.copy')}
                </Button>

                {createdCopy === 'copied' && (
                  <span
                    className="text-sm font-medium text-success"
                    role="status"
                  >
                    {t('projects.copied')}
                  </span>
                )}
                {createdCopy === 'failed' && (
                  <span
                    className="text-sm font-medium text-destructive"
                    role="status"
                  >
                    {t('projects.copyFailed')}
                  </span>
                )}
              </div>
            </div>
          </CardContent>
        </Card>
      )}

      <AlertRulesSection projectId={projectId} />

      <InboundFiltersSection
        projectId={projectId}
        filters={project.inbound_filters ?? EMPTY_INBOUND_FILTERS}
        onSaved={handleProjectUpdated}
      />

      <Card
        role="region"
        aria-labelledby="project-danger-heading"
        className="border-destructive/40"
      >
        <CardHeader>
          <h3
            id="project-danger-heading"
            className="text-base leading-none font-semibold text-destructive"
          >
            {t('projects.detail.danger.heading')}
          </h3>
          <p className="text-sm text-muted-foreground">
            {t('projects.detail.danger.warning', { name: project.name })}
          </p>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex max-w-sm flex-col gap-2">
            <Label htmlFor="project-delete-confirmation">
              {t('projects.detail.danger.confirm')}
            </Label>
            <Input
              id="project-delete-confirmation"
              autoComplete="off"
              value={confirmName}
              onChange={(event) => setConfirmName(event.target.value)}
            />
          </div>

          <div>
            <Button
              type="button"
              variant="destructive"
              disabled={confirmName !== project.name || deleting}
              onClick={() => void handleDelete(project)}
            >
              {t('projects.detail.danger.submit')}
            </Button>
          </div>

          {deleteError !== null && (
            <ErrorState
              error={deleteError}
              prefix={t('projects.detail.danger.error')}
            />
          )}
        </CardContent>
      </Card>
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
    <Card>
      <CardHeader>
        <h3 className="text-base leading-none font-semibold">
          {t('projects.detail.edit.heading')}
        </h3>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <form
          className="flex flex-col gap-4 sm:flex-row sm:items-end"
          onSubmit={(event) => void handleSubmit(event)}
        >
          <div className="flex flex-1 flex-col gap-2">
            <Label htmlFor="project-detail-name">
              {t('projects.detail.edit.name')}
            </Label>
            <Input
              id="project-detail-name"
              required
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={saving || name.trim() === ''}>
              {t('projects.detail.edit.save')}
            </Button>

            {message !== null && (
              <span className="text-sm font-medium text-success" role="status">
                {t(
                  message === 'saved'
                    ? 'projects.detail.edit.saved'
                    : 'projects.detail.edit.noChanges',
                )}
              </span>
            )}
          </div>
        </form>

        {error !== null && (
          <ErrorState
            error={error}
            prefix={t('projects.detail.edit.error')}
          />
        )}
      </CardContent>
    </Card>
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
      <TableRow>
        <TableCell>
          <code className="project-detail-key font-mono text-xs">
            {projectKey.public_key}
          </code>
        </TableCell>
        <TableCell>
          {projectKey.label ?? (
            <span className="text-muted-foreground">
              {t('projects.detail.keys.noLabel')}
            </span>
          )}
        </TableCell>
        <TableCell>
          {projectKey.is_active ? (
            <Badge className="border-transparent bg-success/15 text-success">
              {t('projects.detail.keys.active')}
            </Badge>
          ) : (
            <Badge
              variant="outline"
              className="border-transparent bg-destructive/10 text-destructive"
            >
              {t('projects.detail.keys.revoked')}
              {projectKey.revoked_at !== null && (
                <>
                  {' '}
                  <time dateTime={projectKey.revoked_at}>
                    {formatAbsoluteTime(projectKey.revoked_at, locale)}
                  </time>
                </>
              )}
            </Badge>
          )}
        </TableCell>
        <TableCell>
          <div className="flex flex-wrap items-center gap-2">
            <Input
              className="h-8 w-24"
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
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={savingRate}
              onClick={() => void handleRateSave()}
            >
              {t('projects.detail.keys.save')}
            </Button>
            {rateSaved && (
              <span className="text-sm font-medium text-success" role="status">
                {t('projects.detail.keys.saved')}
              </span>
            )}
          </div>
        </TableCell>
        <TableCell>
          <code className="project-detail-dsn block max-w-80 font-mono text-xs break-all text-muted-foreground">
            {projectKey.dsn}
          </code>
          <div className="mt-1 flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void handleCopy()}
            >
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
        </TableCell>
        <TableCell>
          {projectKey.is_active && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={revoking}
              onClick={() => void handleRevoke()}
            >
              {t('projects.detail.keys.revoke')}
            </Button>
          )}
        </TableCell>
      </TableRow>

      {rateErrorValue !== null && (
        <TableRow>
          <TableCell colSpan={6}>
            <ErrorState
              error={rateErrorValue}
              prefix={t('projects.detail.keys.rateError')}
            />
          </TableCell>
        </TableRow>
      )}

      {revokeError !== null && (
        <TableRow>
          <TableCell colSpan={6}>
            <ErrorState
              error={revokeError}
              prefix={t('projects.detail.keys.revokeError')}
            />
          </TableCell>
        </TableRow>
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
    <Card>
      <CardHeader>
        <h3 className="text-base leading-none font-semibold">
          {t('projects.detail.createKey.heading')}
        </h3>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <form
          className="grid gap-4 sm:grid-cols-2"
          onSubmit={(event) => void handleSubmit(event)}
        >
          <div className="flex flex-col gap-2">
            <Label htmlFor="create-key-label">
              {t('projects.detail.createKey.label')}
            </Label>
            <Input
              id="create-key-label"
              placeholder={t('projects.detail.createKey.labelPlaceholder')}
              value={label}
              onChange={(event) => setLabel(event.target.value)}
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="create-key-rate-limit">
              {t('projects.detail.createKey.rateLimit')}
            </Label>
            <Input
              id="create-key-rate-limit"
              inputMode="numeric"
              placeholder={t('projects.detail.createKey.rateLimitPlaceholder')}
              value={rate}
              onChange={(event) => setRate(event.target.value)}
            />
            <span className="text-xs text-muted-foreground">
              {t('projects.detail.createKey.hint')}
            </span>
          </div>

          <div className="sm:col-span-2">
            <Button type="submit" disabled={creating}>
              {t('projects.detail.createKey.submit')}
            </Button>
          </div>
        </form>

        {errorValue !== null && (
          <ErrorState
            error={errorValue}
            prefix={t('projects.detail.createKey.error')}
          />
        )}
      </CardContent>
    </Card>
  );
}
