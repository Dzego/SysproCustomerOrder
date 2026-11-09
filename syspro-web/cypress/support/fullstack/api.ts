/**
 * Helpers for the full-stack suite. They call the real API through the Angular
 * dev-server proxy, exactly as the application does, to set up and verify data.
 */

export interface CustomerTotal {
  customerId: number;
  legacyCustomerId: string;
  name: string;
  total: number;
}

export interface ImportJob {
  id: number;
  status: 'Pending' | 'Processing' | 'Completed' | 'Failed';
  processed: number;
  created: number;
  updated: number;
  failed: number;
  failureReason: string | null;
}

/** The customers in scripts/learning-env/fixtures/customers_legacy.dat that import successfully. */
export const FIXTURE_CUSTOMERS = [
  { legacyCustomerId: 'E2E0000001', name: 'E2E Alpha Holdings' },
  { legacyCustomerId: 'E2E0000002', name: 'E2E Bravo Traders' },
  { legacyCustomerId: 'E2E0000003', name: 'E2E Charlie Supplies' },
  { legacyCustomerId: 'E2E0000004', name: 'E2E Delta Logistics' },
  { legacyCustomerId: 'E2E0000005', name: 'E2E Echo Retail' },
  { legacyCustomerId: 'E2E0000006', name: 'E2E Foxtrot Zero Spend' },
] as const;

/** Lines in the fixture: 6 valid, 2 invalid and 1 duplicate. */
export const FIXTURE_LINES = { total: 9, valid: 6, rejected: 3 } as const;

/** Never used for an order by any spec, so its spending stays zero. */
export const ZERO_SPEND_CUSTOMER = FIXTURE_CUSTOMERS[5];

/** A short id that makes this run's data distinguishable from earlier runs. */
export const RUN_ID = Date.now().toString(36).toUpperCase();

export const todayUtc = (): string => new Date().toISOString().slice(0, 10);

export function daysAgoUtc(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString().slice(0, 10);
}

export function getTotals(from: string, to: string): Cypress.Chainable<CustomerTotal[]> {
  return cy
    .request<CustomerTotal[]>({
      url: '/api/customers/totals',
      qs: { fromDate: `${from}T00:00:00.0000000Z`, toDate: `${to}T23:59:59.9999999Z` },
    })
    .its('body');
}

export function pollJob(id: number, attempts = 60): Cypress.Chainable<ImportJob> {
  return cy.request<ImportJob>(`/api/import/jobs/${id}`).then(({ body }) => {
    if (body.status === 'Completed' || body.status === 'Failed') {
      return cy.wrap(body);
    }

    if (attempts <= 1) {
      throw new Error(`Import job ${id} did not finish: ${JSON.stringify(body)}`);
    }

    return cy.wait(1000).then(() => pollJob(id, attempts - 1));
  });
}

/** Runs the fixture import through the API when its customers are not there yet. */
export function ensureFixtureCustomers(): void {
  getTotals('2000-01-01', todayUtc()).then((rows) => {
    const missing = FIXTURE_CUSTOMERS.some(
      (fixture) => !rows.some((row) => row.legacyCustomerId === fixture.legacyCustomerId),
    );

    if (missing) {
      cy.request<ImportJob>('POST', '/api/import/customers').then(({ body }) =>
        pollJob(body.id).its('status').should('eq', 'Completed'),
      );
    }
  });
}

export function customerByLegacyId(rows: CustomerTotal[], legacyCustomerId: string): CustomerTotal {
  const row = rows.find((candidate) => candidate.legacyCustomerId === legacyCustomerId);

  if (!row) {
    throw new Error(`Customer ${legacyCustomerId} is not in the database.`);
  }

  return row;
}

/** Sum in whole cents, so the comparison is exact. */
export const cents = (amount: number): number => Math.round(amount * 100);
