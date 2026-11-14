import { useEffect } from 'react';
import { projectId } from './api/client';
import { IssueDetailPage } from './pages/issues/IssueDetailPage';
import { IssuesPage } from './pages/issues/IssuesPage';
import { MessagesPage } from './pages/messages/MessagesPage';
import { navigate, useHashRoute } from './router';

export default function App() {
  const route = useHashRoute();
  // Unknown routes fall back to the issues page, so the Issues tab stays
  // highlighted until the normalising effect replaces the hash.
  const issuesActive = route.name !== 'messages';

  useEffect(() => {
    const hash = window.location.hash;

    if (
      route.name === 'unknown' ||
      hash === '' ||
      hash === '#' ||
      hash === '#/'
    ) {
      navigate('/issues', undefined, { replace: true });
    }
  }, [route]);

  return (
    <>
      <header className="top-bar">
        <h1>glitch-port</h1>
        <nav className="tabs" aria-label="Sections">
          <a
            className="tab"
            href="#/issues"
            aria-current={issuesActive ? 'page' : undefined}
          >
            Issues
          </a>
          <a
            className="tab"
            href="#/messages"
            aria-current={issuesActive ? undefined : 'page'}
          >
            Messages
          </a>
        </nav>
        <span className="project-id">Project {projectId}</span>
      </header>

      <main className="page">
        {route.name === 'messages' && <MessagesPage />}

        {route.name === 'issue' && route.issueId !== null && (
          <IssueDetailPage issueId={route.issueId} />
        )}

        {(route.name === 'issues' || route.name === 'unknown') && <IssuesPage />}
      </main>
    </>
  );
}
