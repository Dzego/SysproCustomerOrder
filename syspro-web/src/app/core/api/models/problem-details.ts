/**
 * RFC 7807 error body. The API returns it (application/problem+json) for every
 * failure: exceptions, 404s and validation errors. ProblemDetailsCustomization
 * adds `traceId` to each one.
 */
export interface ProblemDetails {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string | null;
  readonly instance?: string;
  readonly traceId?: string;
  /** Present on validation failures (ValidationProblemDetails): field name to messages. */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

export function isProblemDetails(value: unknown): value is ProblemDetails {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Record<string, unknown>;

  return typeof candidate['status'] === 'number' || typeof candidate['title'] === 'string';
}
