import { ImportJob, ImportJobStatus } from '../../core/api/models/import-job';

export type BadgeTone = 'info' | 'success' | 'warning' | 'danger';

export const STATUS_TONES: Record<ImportJobStatus, BadgeTone> = {
  Pending: 'info',
  Processing: 'warning',
  Completed: 'success',
  Failed: 'danger',
};

/** One sentence about where the job is, from what the API reports. */
export function describeJob(job: ImportJob): string {
  switch (job.status) {
    case 'Pending':
      // A Pending job with committed lines was interrupted and queued again; it
      // resumes after its last committed batch.
      return job.processed > 0
        ? `Waiting to resume after ${job.processed.toLocaleString('en-US')} committed lines. Interrupted jobs are retried automatically.`
        : 'Queued. Imports run one at a time, so it starts when earlier imports have finished.';
    case 'Processing':
      return `Running: ${job.processed.toLocaleString('en-US')} lines committed so far.`;
    case 'Completed':
      return `Finished: ${job.processed.toLocaleString('en-US')} lines processed.`;
    case 'Failed':
      return job.processed > 0
        ? `Stopped with an error after ${job.processed.toLocaleString('en-US')} lines. Those lines remain applied.`
        : 'Stopped with an error. No customers were changed.';
  }
}
