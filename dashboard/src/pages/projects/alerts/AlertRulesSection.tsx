import { useEffect, useState } from 'react';
import { Pencil, Plus, Send, Trash } from 'lucide-react';
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Card,
  CardContent,
  CardHeader,
} from '@/components/ui/card';
import { Switch } from '@/components/ui/switch';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { cn } from '@/lib/utils';
import {
  deleteAlertRule,
  listAlertNotifications,
  listAlertRules,
  testAlertRule,
  updateAlertRule,
} from '../../../api/client';
import type {
  AlertNotification,
  AlertRule,
  AlertRuleTestResult,
  AlertTrigger,
} from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import type { Translate } from '../../../i18n';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  RelativeTime,
} from '../../../ui';
import { translatedLabel } from '../../../ui/labels';
import { AlertRuleForm } from './AlertRuleForm';

type Loadable<T> =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; data: T };

const KNOWN_TRIGGERS: AlertTrigger[] = [
  'new_issue',
  'regression',
  'event_frequency',
];

function isAlertTrigger(value: string): value is AlertTrigger {
  return (KNOWN_TRIGGERS as string[]).includes(value);
}

function triggerLabel(t: Translate, trigger: string): string {
  return isAlertTrigger(trigger)
    ? t(`projects.detail.alerts.triggers.${trigger}`)
    : trigger;
}

function conditionSummary(t: Translate, rule: AlertRule): string {
  if (rule.trigger !== 'event_frequency') {
    return t(`projects.detail.alerts.triggerSummary.${rule.trigger}`);
  }

  return t('projects.detail.alerts.triggerSummary.event_frequency', {
    threshold: rule.threshold ?? 0,
    window: rule.window_minutes ?? 0,
  });
}

/** Renders the outcome of a test delivery, including a missing status code. */
function testResultText(
  t: Translate,
  result: AlertRuleTestResult,
): string {
  if (result.delivered) {
    return result.status_code === null
      ? t('projects.detail.alerts.testDeliveredNoStatus')
      : t('projects.detail.alerts.testDelivered', {
          status: result.status_code,
        });
  }

  if (result.error === null) {
    return result.status_code === null
      ? t('projects.detail.alerts.testFailedUnknown')
      : t('projects.detail.alerts.testFailedNoError', {
          status: result.status_code,
        });
  }

  return result.status_code === null
    ? t('projects.detail.alerts.testFailed', { error: result.error })
    : t('projects.detail.alerts.testFailedStatus', {
        status: result.status_code,
        error: result.error,
      });
}

/** Notification states map onto the dashboard's semantic colours. */
const STATUS_TONES: Record<string, string> = {
  sent: 'bg-success/15 text-success',
  failed: 'bg-destructive/10 text-destructive',
  pending: 'bg-warning/15 text-warning',
  sending: 'bg-info/15 text-info',
};

function NotificationStatus({ status }: { status: string }) {
  const { t } = useI18n();
  const normalised = status.toLowerCase();
  // Keep the API's mixed-case spelling when the label is not translated.
  const fallback =
    status === '' ? status : status.charAt(0).toUpperCase() + status.slice(1);
  const label = translatedLabel(
    t,
    `projects.detail.alerts.notifications.statuses.${normalised}`,
    fallback,
  );

  return (
    <Badge
      className={cn(
        'alert-notification-status',
        `alert-notification-status-${normalised}`,
        'border-transparent',
        STATUS_TONES[normalised] ?? 'bg-muted text-muted-foreground',
      )}
    >
      {label}
    </Badge>
  );
}

/**
 * Alert rule management of one project.
 *
 * The rule list and the recent notifications are two independent requests: a
 * failure of one never hides the other. Rule mutations and test deliveries
 * report their own errors next to the table so a failed action does not clear
 * the list.
 */
export function AlertRulesSection({ projectId }: { projectId: number }) {
  const { locale, t } = useI18n();
  const [rulesState, setRulesState] = useState<Loadable<AlertRule[]>>({
    status: 'loading',
  });
  const [notificationsState, setNotificationsState] = useState<
    Loadable<AlertNotification[]>
  >({ status: 'loading' });
  const [creating, setCreating] = useState(false);
  const [editingRule, setEditingRule] = useState<AlertRule | null>(null);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [testingId, setTestingId] = useState<number | null>(null);
  const [testResults, setTestResults] = useState<
    Record<number, AlertRuleTestResult>
  >({});
  const [testErrors, setTestErrors] = useState<Record<number, unknown>>({});
  const [mutationError, setMutationError] = useState<{
    prefix: string;
    error: unknown;
  } | null>(null);

  useEffect(() => {
    let cancelled = false;

    setRulesState({ status: 'loading' });
    setNotificationsState({ status: 'loading' });
    setCreating(false);
    setEditingRule(null);
    setConfirmingId(null);
    setMutationError(null);
    setTestResults({});
    setTestErrors({});

    listAlertRules(projectId)
      .then((response) => {
        if (!cancelled) {
          setRulesState({
            status: 'ready',
            data: Array.isArray(response.items) ? response.items : [],
          });
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setRulesState({ status: 'error', error });
        }
      });

    listAlertNotifications(projectId, 50)
      .then((response) => {
        if (!cancelled) {
          setNotificationsState({
            status: 'ready',
            data: Array.isArray(response.items) ? response.items : [],
          });
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setNotificationsState({ status: 'error', error });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [projectId]);

  const replaceRule = (updated: AlertRule) => {
    setRulesState((previous) =>
      previous.status === 'ready'
        ? {
            status: 'ready',
            data: previous.data.map((rule) =>
              rule.id === updated.id ? updated : rule,
            ),
          }
        : previous,
    );
  };

  const handleCreated = (created: AlertRule) => {
    setRulesState((previous) =>
      previous.status === 'ready'
        ? { status: 'ready', data: [...previous.data, created] }
        : previous,
    );
    setCreating(false);
  };

  const handleEdited = (updated: AlertRule) => {
    replaceRule(updated);
    setEditingRule(null);
  };

  const handleToggle = async (rule: AlertRule) => {
    setMutationError(null);

    try {
      const updated = await updateAlertRule(rule.id, {
        is_enabled: !rule.is_enabled,
      });
      replaceRule(updated);
    } catch (error) {
      setMutationError({
        prefix: t('projects.detail.alerts.toggleError'),
        error,
      });
    }
  };

  const handleDelete = async (rule: AlertRule) => {
    setDeletingId(rule.id);
    setMutationError(null);

    try {
      await deleteAlertRule(rule.id);
      setRulesState((previous) =>
        previous.status === 'ready'
          ? {
              status: 'ready',
              data: previous.data.filter((item) => item.id !== rule.id),
            }
          : previous,
      );
      setConfirmingId(null);

      if (editingRule !== null && editingRule.id === rule.id) {
        setEditingRule(null);
      }
    } catch (error) {
      setMutationError({
        prefix: t('projects.detail.alerts.deleteError'),
        error,
      });
    } finally {
      setDeletingId(null);
    }
  };

  const handleTest = async (rule: AlertRule) => {
    setTestingId(rule.id);
    setTestErrors((previous) => {
      const next = { ...previous };
      delete next[rule.id];
      return next;
    });

    try {
      const result = await testAlertRule(rule.id);
      setTestResults((previous) => ({ ...previous, [rule.id]: result }));
    } catch (error) {
      setTestErrors((previous) => ({ ...previous, [rule.id]: error }));
    } finally {
      setTestingId(null);
    }
  };

  const formOpen = creating || editingRule !== null;
  const confirmingRule =
    confirmingId !== null && rulesState.status === 'ready'
      ? (rulesState.data.find((rule) => rule.id === confirmingId) ?? null)
      : null;

  return (
    <Card
      role="region"
      aria-labelledby="project-alert-rules-heading"
      data-testid="alert-rules-section"
    >
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3
            id="project-alert-rules-heading"
            className="text-base leading-none font-semibold"
          >
            {t('projects.detail.alerts.heading')}
          </h3>
          <Button
            type="button"
            size="sm"
            onClick={() => {
              setEditingRule(null);
              setCreating(true);
              setMutationError(null);
            }}
          >
            <Plus aria-hidden="true" />
            {t('projects.detail.alerts.create')}
          </Button>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {formOpen && (
          <AlertRuleForm
            key={editingRule === null ? 'create' : editingRule.id}
            projectId={projectId}
            rule={editingRule}
            onSaved={editingRule === null ? handleCreated : handleEdited}
            onCancel={() => {
              setCreating(false);
              setEditingRule(null);
            }}
          />
        )}

        {rulesState.status === 'loading' && <LoadingState />}

        {rulesState.status === 'error' && (
          <ErrorState
            error={rulesState.error}
            prefix={t('projects.detail.alerts.loadError')}
          />
        )}

        {rulesState.status === 'ready' && rulesState.data.length === 0 && (
          <EmptyState>{t('projects.detail.alerts.empty')}</EmptyState>
        )}

        {rulesState.status === 'ready' && rulesState.data.length > 0 && (
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow className="bg-muted/50 hover:bg-muted/50">
                  <TableHead>{t('projects.detail.alerts.name')}</TableHead>
                  <TableHead>{t('projects.detail.alerts.trigger')}</TableHead>
                  <TableHead>{t('projects.detail.alerts.condition')}</TableHead>
                  <TableHead>{t('projects.detail.alerts.channel')}</TableHead>
                  <TableHead>{t('projects.detail.alerts.enabled')}</TableHead>
                  <TableHead>{t('projects.detail.alerts.actions')}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rulesState.data.map((rule) => {
                  const testResult = testResults[rule.id];
                  const testError = testErrors[rule.id];

                  return (
                    <TableRow key={rule.id}>
                      <TableCell className="font-medium">{rule.name}</TableCell>
                      <TableCell>{triggerLabel(t, rule.trigger)}</TableCell>
                      <TableCell>{conditionSummary(t, rule)}</TableCell>
                      <TableCell>
                        {t(`projects.detail.alerts.channels.${rule.channel}`)}
                      </TableCell>
                      <TableCell>
                        <Switch
                          className="p-0"
                          aria-label={t('projects.detail.alerts.enabledFor', {
                            name: rule.name,
                          })}
                          checked={rule.is_enabled}
                          onCheckedChange={() => void handleToggle(rule)}
                        />
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap items-center gap-2">
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            onClick={() => {
                              setCreating(false);
                              setEditingRule(rule);
                              setMutationError(null);
                            }}
                          >
                            <Pencil aria-hidden="true" />
                            {t('projects.detail.alerts.edit')}
                          </Button>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            disabled={testingId === rule.id}
                            onClick={() => void handleTest(rule)}
                          >
                            <Send aria-hidden="true" />
                            {t('projects.detail.alerts.test')}
                          </Button>

                          <Button
                            type="button"
                            variant="destructive"
                            size="sm"
                            onClick={() => setConfirmingId(rule.id)}
                          >
                            <Trash aria-hidden="true" />
                            {t('projects.detail.alerts.delete')}
                          </Button>
                        </div>

                        {testResult !== undefined && (
                          <p
                            className={cn(
                              'mt-2 text-sm font-medium',
                              testResult.delivered
                                ? 'text-success'
                                : 'text-destructive',
                            )}
                            role="status"
                          >
                            {testResultText(t, testResult)}
                          </p>
                        )}

                        {testError !== undefined && (
                          <ErrorState
                            error={testError}
                            prefix={t('projects.detail.alerts.testError')}
                          />
                        )}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}

        {confirmingRule !== null && (
          <AlertDialog
            open
            onOpenChange={(open) => {
              if (!open) {
                setConfirmingId(null);
              }
            }}
          >
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>
                  {t('projects.detail.alerts.deleteDialogTitle')}
                </AlertDialogTitle>
                <AlertDialogDescription>
                  {t('projects.detail.alerts.deleteConfirm', {
                    name: confirmingRule.name,
                  })}
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>
                  {t('projects.detail.alerts.cancel')}
                </AlertDialogCancel>
                <AlertDialogAction
                  variant="destructive"
                  disabled={deletingId === confirmingRule.id}
                  onClick={() => void handleDelete(confirmingRule)}
                >
                  {t('projects.detail.alerts.confirmDelete')}
                </AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>
        )}

        {mutationError !== null && (
          <ErrorState
            error={mutationError.error}
            prefix={mutationError.prefix}
          />
        )}

        <div className="flex flex-col gap-3 border-t pt-4">
          <h4 className="text-sm font-semibold">
            {t('projects.detail.alerts.notifications.heading')}
          </h4>

          {notificationsState.status === 'loading' && <LoadingState />}

          {notificationsState.status === 'error' && (
            <ErrorState
              error={notificationsState.error}
              prefix={t('projects.detail.alerts.notifications.loadError')}
            />
          )}

          {notificationsState.status === 'ready' &&
            notificationsState.data.length === 0 && (
              <EmptyState>
                {t('projects.detail.alerts.notifications.empty')}
              </EmptyState>
            )}

          {notificationsState.status === 'ready' &&
            notificationsState.data.length > 0 && (
              <div className="overflow-hidden rounded-lg border">
                <Table>
                  <TableHeader>
                    <TableRow className="bg-muted/50 hover:bg-muted/50">
                      <TableHead>
                        {t('projects.detail.alerts.notifications.createdAt')}
                      </TableHead>
                      <TableHead>
                        {t('projects.detail.alerts.notifications.rule')}
                      </TableHead>
                      <TableHead>
                        {t('projects.detail.alerts.notifications.trigger')}
                      </TableHead>
                      <TableHead>
                        {t('projects.detail.alerts.notifications.status')}
                      </TableHead>
                      <TableHead className="text-right">
                        {t('projects.detail.alerts.notifications.attempts')}
                      </TableHead>
                      <TableHead>
                        {t('projects.detail.alerts.notifications.sentAt')}
                      </TableHead>
                      <TableHead>
                        {t('projects.detail.alerts.notifications.lastError')}
                      </TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {notificationsState.data.map((notification) => (
                      <TableRow key={notification.id}>
                        <TableCell>
                          <RelativeTime value={notification.created_at} />
                        </TableCell>
                        <TableCell>{notification.rule_name}</TableCell>
                        <TableCell>
                          {triggerLabel(t, notification.trigger)}
                        </TableCell>
                        <TableCell>
                          <NotificationStatus status={notification.status} />
                        </TableCell>
                        <TableCell className="alert-notification-attempts text-right tabular-nums">
                          {formatNumber(notification.attempts, locale)}
                        </TableCell>
                        <TableCell>
                          {notification.sent_at === null ? (
                            <span className="text-muted-foreground">—</span>
                          ) : (
                            <RelativeTime value={notification.sent_at} />
                          )}
                        </TableCell>
                        <TableCell>
                          {notification.last_error === null ? (
                            <span className="text-muted-foreground">—</span>
                          ) : (
                            <span className="alert-notification-error break-words text-destructive">
                              {notification.last_error}
                            </span>
                          )}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}
        </div>
      </CardContent>
    </Card>
  );
}
