export function IssueDetailPage({ issueId }: { issueId: number }) {
  return (
    <section className="issue-detail-page" data-testid="issue-detail-page">
      <h2>Issue {issueId}</h2>
    </section>
  );
}
