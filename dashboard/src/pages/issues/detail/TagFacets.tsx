import { useEffect, useState } from 'react';
import { getIssueTags } from '../../../api/client';
import type {
  IssueTagFacet,
  IssueTagValue,
  IssueTagsResponse,
} from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import { ErrorState, LoadingState } from '../../../ui';
import { Card } from '@/components/ui/card';

/**
 * Fixed facet keys map to translated labels. Payload tag keys (`tags.<name>`)
 * have no known translation, so they keep the raw API key.
 */
const TAG_KEY_LABELS: Record<string, string> = {
  release: 'issues.detail.tags.keys.release',
  environment: 'issues.detail.tags.keys.environment',
  platform: 'issues.detail.tags.keys.platform',
  'os.name': 'issues.detail.tags.keys.osName',
  'device.model': 'issues.detail.tags.keys.deviceModel',
  'gpu.name': 'issues.detail.tags.keys.gpuName',
  'gpu.api': 'issues.detail.tags.keys.gpuApi',
  'runtime.version': 'issues.detail.tags.keys.runtimeVersion',
};

type TagsState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; tags: IssueTagsResponse };

function percentage(count: number, total: number): number {
  if (total <= 0) {
    return 0;
  }

  return Math.round((count / total) * 100);
}

/**
 * Tag distribution of an issue: the fixed facets (release, environment,
 * platform, engine contexts) plus the most frequent SDK payload tags.
 *
 * The request and its failure state are local to this panel so a failing tags
 * endpoint cannot take down the rest of the issue detail screen.
 */
export function TagFacets({ issueId }: { issueId: number }) {
  const { locale, t } = useI18n();
  const [state, setState] = useState<TagsState>({ status: 'loading' });

  useEffect(() => {
    let cancelled = false;

    setState({ status: 'loading' });

    getIssueTags(issueId)
      .then((response) => {
        if (!cancelled) {
          setState({ status: 'ready', tags: response });
        }
      })
      .catch((error) => {
        if (!cancelled) {
          setState({ status: 'error', error });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [issueId]);

  const renderFacet = (facet: IssueTagFacet) => {
    const labelKey = TAG_KEY_LABELS[facet.key];

    return (
      <Card
        className="gap-2 border-border/60 p-4 shadow-none"
        data-testid="tag-facet"
        key={facet.key}
      >
        <h4 className="m-0 flex items-baseline justify-between gap-2 text-sm">
          <span className="font-semibold">
            {labelKey === undefined ? facet.key : t(labelKey)}
          </span>
          <span className="text-xs font-normal text-muted-foreground tabular-nums">
            {formatNumber(facet.total, locale)}
          </span>
        </h4>

        {facet.top.length === 0 ? (
          <p className="m-0 text-sm text-muted-foreground">
            {t('issues.detail.tags.emptyFacet')}
          </p>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {facet.top.map((value: IssueTagValue) => {
              const pct = percentage(value.count, facet.total);

              return (
                <li
                  className="issue-tag-value grid grid-cols-[minmax(4rem,1fr)_3rem_3.5rem] items-center gap-2 text-xs sm:grid-cols-[minmax(6rem,14rem)_minmax(4rem,1fr)_3rem_3.5rem]"
                  key={value.value}
                >
                  <span className="truncate">{value.value}</span>
                  <span
                    className="hidden h-2 overflow-hidden rounded-full bg-muted sm:block"
                    aria-hidden="true"
                  >
                    <span
                      className="issue-tag-value-bar block h-full rounded-full bg-primary"
                      style={{ width: `${pct}%` }}
                    />
                  </span>
                  <span className="text-right text-muted-foreground tabular-nums">
                    {pct}%
                  </span>
                  <span className="text-right text-muted-foreground tabular-nums">
                    {formatNumber(value.count, locale)}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </Card>
    );
  };

  return (
    <Card
      className="gap-3 p-6"
      data-testid="issue-tags"
      aria-labelledby="issue-tags-heading"
    >
      <h3 id="issue-tags-heading" className="m-0 text-base font-semibold">
        {t('issues.detail.tags.title')}
      </h3>

      {state.status === 'loading' && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState
          error={state.error}
          prefix={t('issues.detail.tags.error')}
        />
      )}

      {state.status === 'ready' && (
        <>
          <p className="m-0 flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted-foreground">
            <span>
              {t('issues.detail.tags.sampleSize', {
                count: state.tags.sample_size,
              })}
            </span>
            <span>
              {t('issues.detail.tags.userCount', {
                count: state.tags.user_count,
              })}
            </span>
          </p>

          {state.tags.tags.length === 0 ? (
            <p className="m-0 text-sm text-muted-foreground">
              {t('issues.detail.tags.empty')}
            </p>
          ) : (
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {state.tags.tags.map(renderFacet)}
            </div>
          )}
        </>
      )}
    </Card>
  );
}
