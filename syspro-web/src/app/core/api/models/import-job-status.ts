import { ImportJob, ImportJobStatus } from './import-job';

/** A job in one of these states will not change again. */
const TERMINAL_STATUSES: readonly ImportJobStatus[] = ['Completed', 'Failed'];

export function isTerminalStatus(status: ImportJobStatus): boolean {
  return TERMINAL_STATUSES.includes(status);
}

export function isTerminalJob(job: ImportJob): boolean {
  return isTerminalStatus(job.status);
}
