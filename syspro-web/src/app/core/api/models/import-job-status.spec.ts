import { isTerminalStatus } from './import-job-status';

describe('isTerminalStatus', () => {
  it.each([
    ['Pending', false],
    ['Processing', false],
    ['Completed', true],
    ['Failed', true],
  ] as const)('%s is terminal: %s', (status, expected) => {
    expect(isTerminalStatus(status)).toBe(expected);
  });
});
