import { setupZoneTestEnv } from 'jest-preset-angular/setup-env/zone';

// The application uses zone.js change detection, so the tests do too.
setupZoneTestEnv({
  errorOnUnknownElements: true,
  errorOnUnknownProperties: true,
});
