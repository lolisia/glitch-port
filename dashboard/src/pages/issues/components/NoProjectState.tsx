import { useI18n } from '../../../i18n';
import { EmptyState } from '../../../ui';

/**
 * Shown by project-scoped screens while no project is active: before the
 * project list has loaded, when it is empty, or when loading it failed. The
 * screens must not call the API in that state.
 */
export function NoProjectState() {
  const { t } = useI18n();

  return (
    <EmptyState>
      {t('issues.noProject')} —{' '}
      <a href="#/projects">{t('issues.chooseProject')}</a>{' '}
      {t('issues.noProjectSuffix')}
    </EmptyState>
  );
}
