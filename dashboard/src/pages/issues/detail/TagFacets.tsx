import { useEffect, useState } from 'react';
import { getIssueTags } from '../../../api/client';
import type { IssueTagFacet, IssueTagValue, IssueTagsResponse } from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import { ErrorState, LoadingState } from '../../../ui';

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
      <div
        className="issue-tag-facet"
        data-testid="tag-facet"
        key={facet.key}
      >
        <h4 className="issue-tag-facet-heading">
          <span className="issue-tag-facet-key">
            {labelKey === undefined ? facet.key : t(labelKey)}
          </span>
          <span className="issue-tag-facet-total">
            {formatNumber(facet.total, locale)}
          </span>
        </h4>

        {facet.top.length === 0 ? (
          <p className="issue-tag-facet-empty">
            {t('issues.detail.tags.emptyFacet')}
          </p>
        ) : (
          <ul className="issue-tag-values">
            {facet.top.map((value: IssueTagValue) => {
              const pct = percentage(value.count, facet.total);

              return (
                <li className="issue-tag-value" key={value.value}>
                  <span className="issue-tag-value-label">{value.value}</span>
                  <span className="issue-tag-value-bar-track" aria-hidden="true">
                    <span
                      className="issue-tag-value-bar"
                      style={{ width: `${pct}%` }}
                    />
                  </span>
                  <span className="issue-tag-value-percentage">{pct}%</span>
                  <span className="issue-tag-value-count">
                    {formatNumber(value.count, locale)}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    );
  };

  return (
    <section
      className="issue-tags"
      data-testid="issue-tags"
      aria-labelledby="issue-tags-heading"
    >
      <h3 id="issue-tags-heading">{t('issues.detail.tags.title')}</h3>

      {state.status === 'loading' && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState
          error={state.error}
          prefix={t('issues.detail.tags.error')}
        />
      )}

      {state.status === 'ready' && (
        <>
          <p className="issue-tags-summary">
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
            <p className="issue-tags-empty">{t('issues.detail.tags.empty')}</p>
          ) : (
            <div className="issue-tag-facets">
              {state.tags.tags.map(renderFacet)}
            </div>
          )}
        </>
      )}
    </section>
  );
}
