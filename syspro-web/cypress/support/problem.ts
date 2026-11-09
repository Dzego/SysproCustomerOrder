/** A ProblemDetails body in the shape the API returns, for stubbed failures. */
export function problem(status: number, detail: string, traceId = `00-fixture-${status}-00`) {
  return {
    statusCode: status,
    headers: { 'content-type': 'application/problem+json' },
    body: { type: 'about:blank', title: `Status ${status}`, status, detail, traceId },
  };
}
