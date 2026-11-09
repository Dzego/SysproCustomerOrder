// @ts-check
const { createCjsPreset } = require('jest-preset-angular/presets');

/** @type {import('jest').Config} */
module.exports = {
  ...createCjsPreset({
    tsconfig: '<rootDir>/tsconfig.spec.json',
    // The preset's own environment runs on the project's jsdom, so the separate
    // jest-environment-jsdom package is not needed.
    testEnvironment: 'jest-preset-angular/environments/jest-jsdom-env',
  }),
  setupFilesAfterEnv: ['<rootDir>/setup-jest.ts'],
  roots: ['<rootDir>/src'],
  testMatch: ['**/*.spec.ts'],
  // Every test starts from a clean slate, so no mock state leaks between tests.
  clearMocks: true,
  restoreMocks: true,
  collectCoverageFrom: [
    'src/app/**/*.ts',
    '!src/app/**/*.module.ts',
    '!src/app/**/*.spec.ts',
    '!src/app/**/index.ts',
  ],
  coverageDirectory: '<rootDir>/coverage',
  coverageReporters: ['text-summary', 'html', 'lcov'],
};
