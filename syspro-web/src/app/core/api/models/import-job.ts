/**
 * Lifecycle of a customer import job (C# ImportJobStatus, serialised by name):
 * Pending -> Processing -> Completed or Failed, and Processing -> Pending when an
 * interrupted job is queued to run again.
 */
export type ImportJobStatus = 'Pending' | 'Processing' | 'Completed' | 'Failed';

/** Response of POST /api/import/customers (202) and GET /api/import/jobs/{id}. */
export interface ImportJob {
  readonly id: number;
  readonly status: ImportJobStatus;
  /** ISO 8601 UTC timestamps, always with a trailing "Z". */
  readonly createdAt: string;
  readonly startedAt: string | null;
  readonly completedAt: string | null;
  /**
   * Counts of what has been committed so far: they grow while the job runs, and
   * a Failed job can have non-zero counts for the batches it applied.
   */
  readonly processed: number;
  readonly created: number;
  readonly updated: number;
  readonly failed: number;
  readonly failureReason: string | null;
}
