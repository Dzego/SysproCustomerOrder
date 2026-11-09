import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';
import { CustomerFilters, pageCustomers } from './customer-spending';

const row = (
  customerId: number,
  name: string,
  total: number,
  legacy = `${customerId}`.padStart(10, '0'),
): CustomerOrderTotal => ({
  customerId,
  legacyCustomerId: legacy,
  name,
  total,
});

const rows = [
  row(1, 'Acme Trading', 250.25),
  row(2, 'globex', 59.97),
  row(3, 'Initech', 0),
  row(4, 'Bluth Company', 59.97),
  row(5, 'Wayne Enterprises', 0),
];

const filters = (changes: Partial<CustomerFilters> = {}): CustomerFilters => ({
  search: '',
  hideZero: false,
  sort: 'total-desc',
  pageSize: 25,
  ...changes,
});

const names = (page: ReturnType<typeof pageCustomers>): string[] => page.rows.map((r) => r.name);

describe('pageCustomers', () => {
  it('includes customers with zero spending by default, highest first and ties by name', () => {
    const page = pageCustomers(rows, filters(), 0);

    expect(names(page)).toEqual([
      'Acme Trading',
      'Bluth Company',
      'globex',
      'Initech',
      'Wayne Enterprises',
    ]);
    expect(page).toMatchObject({
      totalCount: 5,
      matchCount: 5,
      zeroCount: 2,
      firstRow: 1,
      lastRow: 5,
      pageCount: 1,
    });
  });

  it('hides customers without orders when asked', () => {
    expect(names(pageCustomers(rows, filters({ hideZero: true }), 0))).toEqual([
      'Acme Trading',
      'Bluth Company',
      'globex',
    ]);
  });

  it('searches names and legacy ids without regard to case', () => {
    expect(names(pageCustomers(rows, filters({ search: 'GLOB' }), 0))).toEqual(['globex']);
    expect(names(pageCustomers(rows, filters({ search: ' 0000000004 ' }), 0))).toEqual([
      'Bluth Company',
    ]);
  });

  it.each([
    ['total-asc', ['Initech', 'Wayne Enterprises', 'Bluth Company', 'globex', 'Acme Trading']],
    ['name-asc', ['Acme Trading', 'Bluth Company', 'globex', 'Initech', 'Wayne Enterprises']],
    ['name-desc', ['Wayne Enterprises', 'Initech', 'globex', 'Bluth Company', 'Acme Trading']],
    ['legacy-asc', ['Acme Trading', 'globex', 'Initech', 'Bluth Company', 'Wayne Enterprises']],
  ] as const)('sorts by %s', (sort, expected) => {
    expect(names(pageCustomers(rows, filters({ sort }), 0))).toEqual(expected);
  });

  it('pages the matches and keeps the page within range', () => {
    const many = Array.from({ length: 60 }, (_, i) =>
      row(i + 1, `Customer ${String(i + 1).padStart(2, '0')}`, 0),
    );

    const second = pageCustomers(many, filters({ sort: 'name-asc' }), 1);
    expect(second).toMatchObject({ pageIndex: 1, pageCount: 3, firstRow: 26, lastRow: 50 });
    expect(second.rows[0].name).toBe('Customer 26');

    expect(pageCustomers(many, filters(), 99)).toMatchObject({
      pageIndex: 2,
      firstRow: 51,
      lastRow: 60,
    });
    expect(pageCustomers(many, filters(), -3).pageIndex).toBe(0);
  });

  it('reports an empty page when nothing matches', () => {
    expect(pageCustomers(rows, filters({ search: 'zzz' }), 0)).toMatchObject({
      matchCount: 0,
      rows: [],
      firstRow: 0,
      lastRow: 0,
      pageCount: 1,
    });
  });
});
