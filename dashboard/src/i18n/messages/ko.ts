import type { Messages } from './en';
import { event } from './ko/event';
import { issues } from './ko/issues';
import { projects } from './ko/projects';

/**
 * Korean messages. Declaring the object as {@link Messages} makes `tsc` fail
 * when a key is missing, misspelled or empty relative to the English source.
 *
 * Korean has a single plural category (`other`), so the `_one` and `_other`
 * entries intentionally carry the same text.
 */
export const ko: Messages = {
  nav: {
    sections: '섹션',
    issues: '이슈',
    messages: '메시지',
    projects: '프로젝트',
  },
  common: {
    language: '언어',
    theme: '테마',
    themeSystem: '시스템',
    themeLight: '라이트',
    themeDark: '다크',
    project: '프로젝트',
    refresh: '새로고침',
    loading: '불러오는 중...',
  },
  app: {
    projectsLoading: '프로젝트를 불러오는 중...',
    projectsUnavailable: '프로젝트를 사용할 수 없습니다.',
    noProjects: '아직 프로젝트가 없습니다',
    createOne: '하나 만들기',
  },
  ui: {
    level: {
      debug: '디버그',
      info: '정보',
      warning: '경고',
      error: '오류',
      fatal: '치명적',
      trace: '추적',
      unknown: '알 수 없음',
    },
    status: {
      unresolved: '미해결',
      resolved: '해결됨',
      ignored: '무시됨',
    },
    justNow: '방금 전',
    tokenHint: '{name}에 collector가 허용하는 관리자 토큰을 설정하세요.',
    eventCount_one: '{count}개 이벤트',
    eventCount_other: '{count}개 이벤트',
  },
  messages: {
    title: '메시지',
    receivedAt: '수신 시각',
    endpoint: '엔드포인트',
    message: '메시지',
    empty: '아직 메시지가 없습니다.',
    loadError: '메시지를 불러오지 못했습니다',
  },
  issues,
  event,
  projects,
};
