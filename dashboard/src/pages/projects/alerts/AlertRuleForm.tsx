import { useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { createAlertRule, updateAlertRule } from '../../../api/client';
import type {
  AlertChannel,
  AlertRule,
  AlertTrigger,
  CreateAlertRuleRequest,
  IssueLevel,
  UpdateAlertRuleRequest,
} from '../../../api/types';
import { useI18n } from '../../../i18n';
import { ErrorState } from '../../../ui';

const TRIGGERS: AlertTrigger[] = ['new_issue', 'regression', 'event_frequency'];
const CHANNELS: AlertChannel[] = ['webhook', 'discord', 'slack'];
const LEVELS: IssueLevel[] = ['debug', 'info', 'warning', 'error', 'fatal'];

/** Parsed values of a complete rule, ready to build a create or update body. */
interface ParsedRule {
  name: string;
  trigger: AlertTrigger;
  channel: AlertChannel;
  targetUrl: string;
  cooldownMinutes: number;
  minLevel: IssueLevel | null;
  environment: string | null;
  threshold: number | null;
  windowMinutes: number | null;
}

type ParseResult =
  | { ok: true; value: ParsedRule }
  | { ok: false; error: string };

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function parsePositiveInteger(value: string): number | null {
  const trimmed = value.trim();

  if (!/^\d+$/.test(trimmed)) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function parseIntInRange(value: string, min: number, max: number): number | null {
  const parsed = parsePositiveInteger(value);
  return parsed !== null && parsed >= min && parsed <= max ? parsed : null;
}

interface AlertRuleFormProps {
  projectId: number;
  /** `null` creates a new rule, otherwise the rule is edited. */
  rule: AlertRule | null;
  onSaved: (rule: AlertRule) => void;
  onCancel: () => void;
}

/**
 * Create/edit form shared by both alert rule mutations.
 *
 * `threshold` and `window_minutes` are only rendered (and only submitted) for
 * `event_frequency`: switching to another trigger clears both fields so a
 * stale value can never reach the API.
 */
export function AlertRuleForm({
  projectId,
  rule,
  onSaved,
  onCancel,
}: AlertRuleFormProps) {
  const { t } = useI18n();
  const [name, setName] = useState(rule?.name ?? '');
  const [trigger, setTrigger] = useState<AlertTrigger>(
    rule?.trigger ?? 'new_issue',
  );
  const [channel, setChannel] = useState<AlertChannel>(
    rule?.channel ?? 'webhook',
  );
  const [targetUrl, setTargetUrl] = useState(rule?.target_url ?? '');
  const [threshold, setThreshold] = useState(
    rule?.threshold === null || rule?.threshold === undefined
      ? ''
      : String(rule.threshold),
  );
  const [windowMinutes, setWindowMinutes] = useState(
    rule?.window_minutes === null || rule?.window_minutes === undefined
      ? ''
      : String(rule.window_minutes),
  );
  const [cooldownMinutes, setCooldownMinutes] = useState(
    String(rule?.cooldown_minutes ?? 60),
  );
  const [minLevel, setMinLevel] = useState<string>(rule?.min_level ?? '');
  const [environment, setEnvironment] = useState(rule?.environment ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const handleTriggerChange = (next: AlertTrigger) => {
    setTrigger(next);

    if (next !== 'event_frequency') {
      setThreshold('');
      setWindowMinutes('');
    }
  };

  const parse = (): ParseResult => {
    const trimmedName = name.trim();
    if (trimmedName === '') {
      return { ok: false, error: t('projects.detail.alerts.form.errors.name') };
    }

    const trimmedUrl = targetUrl.trim();
    if (!isHttpUrl(trimmedUrl)) {
      return { ok: false, error: t('projects.detail.alerts.form.errors.url') };
    }

    const cooldown = parseIntInRange(cooldownMinutes, 1, 10080);
    if (cooldown === null) {
      return {
        ok: false,
        error: t('projects.detail.alerts.form.errors.cooldown'),
      };
    }

    let parsedThreshold: number | null = null;
    let parsedWindow: number | null = null;

    if (trigger === 'event_frequency') {
      parsedThreshold = parsePositiveInteger(threshold);
      if (parsedThreshold === null || parsedThreshold < 1) {
        return {
          ok: false,
          error: t('projects.detail.alerts.form.errors.threshold'),
        };
      }

      parsedWindow = parseIntInRange(windowMinutes, 1, 1440);
      if (parsedWindow === null) {
        return {
          ok: false,
          error: t('projects.detail.alerts.form.errors.window'),
        };
      }
    }

    const trimmedEnvironment = environment.trim();

    return {
      ok: true,
      value: {
        name: trimmedName,
        trigger,
        channel,
        targetUrl: trimmedUrl,
        cooldownMinutes: cooldown,
        minLevel: minLevel === '' ? null : (minLevel as IssueLevel),
        environment:
          trimmedEnvironment === '' ? null : trimmedEnvironment,
        threshold: parsedThreshold,
        windowMinutes: parsedWindow,
      },
    };
  };

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (saving) {
      return;
    }

    const parsed = parse();
    if (!parsed.ok) {
      setError(new Error(parsed.error));
      return;
    }

    const value = parsed.value;
    setSaving(true);
    setError(null);

    try {
      let saved: AlertRule;

      if (rule === null) {
        const body: CreateAlertRuleRequest = {
          name: value.name,
          trigger: value.trigger,
          channel: value.channel,
          target_url: value.targetUrl,
          cooldown_minutes: value.cooldownMinutes,
        };

        if (value.minLevel !== null) {
          body.min_level = value.minLevel;
        }
        if (value.environment !== null) {
          body.environment = value.environment;
        }
        // Non-frequency triggers omit both fields entirely; the API rejects
        // them together with a non-frequency trigger.
        if (value.trigger === 'event_frequency') {
          body.threshold = value.threshold;
          body.window_minutes = value.windowMinutes;
        }

        saved = await createAlertRule(projectId, body);
      } else {
        // A PATCH carries every editable field: explicit nulls clear the
        // optional level/environment and the frequency fields after a trigger
        // change.
        const body: UpdateAlertRuleRequest = {
          name: value.name,
          trigger: value.trigger,
          channel: value.channel,
          target_url: value.targetUrl,
          cooldown_minutes: value.cooldownMinutes,
          min_level: value.minLevel,
          environment: value.environment,
          threshold: value.threshold,
          window_minutes: value.windowMinutes,
        };

        saved = await updateAlertRule(rule.id, body);
      }

      onSaved(saved);
    } catch (saveError) {
      setError(saveError);
    } finally {
      setSaving(false);
    }
  };

  return (
    <form
      className="flex flex-col gap-4 rounded-lg border bg-muted/30 p-4"
      aria-label={
        rule === null
          ? t('projects.detail.alerts.createHeading')
          : t('projects.detail.alerts.editHeading')
      }
      onSubmit={(event) => void handleSubmit(event)}
    >
      <h3 className="text-base leading-none font-semibold">
        {rule === null
          ? t('projects.detail.alerts.createHeading')
          : t('projects.detail.alerts.editHeading')}
      </h3>

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        <div className="flex flex-col gap-2">
          <Label htmlFor="alert-rule-name">
            {t('projects.detail.alerts.name')}
          </Label>
          <Input
            id="alert-rule-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-2 [&_[data-slot=native-select-wrapper]]:w-full">
          <Label htmlFor="alert-rule-trigger">
            {t('projects.detail.alerts.trigger')}
          </Label>
          <NativeSelect
            id="alert-rule-trigger"
            value={trigger}
            onChange={(event) =>
              handleTriggerChange(event.target.value as AlertTrigger)
            }
          >
            {TRIGGERS.map((value) => (
              <NativeSelectOption key={value} value={value}>
                {t(`projects.detail.alerts.triggers.${value}`)}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </div>

        {trigger === 'event_frequency' && (
          <>
            <div className="flex flex-col gap-2">
              <Label htmlFor="alert-rule-threshold">
                {t('projects.detail.alerts.form.threshold')}
              </Label>
              <Input
                id="alert-rule-threshold"
                inputMode="numeric"
                value={threshold}
                onChange={(event) => setThreshold(event.target.value)}
              />
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="alert-rule-window">
                {t('projects.detail.alerts.form.window')}
              </Label>
              <Input
                id="alert-rule-window"
                inputMode="numeric"
                value={windowMinutes}
                onChange={(event) => setWindowMinutes(event.target.value)}
              />
            </div>
          </>
        )}

        <div className="flex flex-col gap-2 [&_[data-slot=native-select-wrapper]]:w-full">
          <Label htmlFor="alert-rule-channel">
            {t('projects.detail.alerts.channel')}
          </Label>
          <NativeSelect
            id="alert-rule-channel"
            value={channel}
            onChange={(event) =>
              setChannel(event.target.value as AlertChannel)
            }
          >
            {CHANNELS.map((value) => (
              <NativeSelectOption key={value} value={value}>
                {t(`projects.detail.alerts.channels.${value}`)}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </div>

        <div className="flex flex-col gap-2 sm:col-span-2 xl:col-span-2">
          <Label htmlFor="alert-rule-target">
            {t('projects.detail.alerts.form.targetUrl')}
          </Label>
          <Input
            id="alert-rule-target"
            inputMode="url"
            placeholder="https://hooks.example.com/alerts"
            value={targetUrl}
            onChange={(event) => setTargetUrl(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-2 [&_[data-slot=native-select-wrapper]]:w-full">
          <Label htmlFor="alert-rule-min-level">
            {t('projects.detail.alerts.form.minLevel')}
          </Label>
          <NativeSelect
            id="alert-rule-min-level"
            value={minLevel}
            onChange={(event) => setMinLevel(event.target.value)}
          >
            <NativeSelectOption value="">
              {t('projects.detail.alerts.form.minLevelAny')}
            </NativeSelectOption>
            {LEVELS.map((value) => (
              <NativeSelectOption key={value} value={value}>
                {t(`ui.level.${value}`)}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="alert-rule-environment">
            {t('projects.detail.alerts.form.environment')}
          </Label>
          <Input
            id="alert-rule-environment"
            placeholder={t('projects.detail.alerts.form.environmentPlaceholder')}
            value={environment}
            onChange={(event) => setEnvironment(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="alert-rule-cooldown">
            {t('projects.detail.alerts.form.cooldown')}
          </Label>
          <Input
            id="alert-rule-cooldown"
            inputMode="numeric"
            value={cooldownMinutes}
            onChange={(event) => setCooldownMinutes(event.target.value)}
          />
          <span className="text-xs text-muted-foreground">
            {t('projects.detail.alerts.form.cooldownHint')}
          </span>
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={saving}>
          {t('projects.detail.alerts.save')}
        </Button>
        <Button type="button" variant="outline" onClick={onCancel} disabled={saving}>
          {t('projects.detail.alerts.cancel')}
        </Button>
      </div>

      {error !== null && (
        <ErrorState
          error={error}
          prefix={t('projects.detail.alerts.saveError')}
        />
      )}
    </form>
  );
}
