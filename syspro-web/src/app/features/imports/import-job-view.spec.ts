import { ImportJob } from '../../core/api/models/import-job';
import { describeJob } from './import-job-view';

const job = (changes: Partial<ImportJob>): ImportJob => ({
  id: 1,
  status: 'Pending',
  createdAt: '2026-11-09T10:00:00Z',
  startedAt: null,
  completedAt: null,
  processed: 0,
  created: 0,
  updated: 0,
  failed: 0,
  failureReason: null,
  ...changes,
});

describe('describeJob', () => {
  it('distinguishes a queued job from one waiting to resume', () => {
    expect(describeJob(job({}))).toContain('Queued');
    expect(describeJob(job({ processed: 2000 }))).toContain('resume after 2,000 committed lines');
  });

  it('describes running, finished and failed jobs', () => {
    expect(describeJob(job({ status: 'Processing', processed: 1500 }))).toContain('1,500 lines committed');
    expect(describeJob(job({ status: 'Completed', processed: 60000 }))).toContain('60,000 lines processed');
    expect(describeJob(job({ status: 'Failed' }))).toContain('No customers were changed');
    expect(describeJob(job({ status: 'Failed', processed: 1000 }))).toContain('Those lines remain applied');
  });
});
