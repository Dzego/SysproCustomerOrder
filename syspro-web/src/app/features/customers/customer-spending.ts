import { CustomerOrderTotal } from '../../core/api/models/customer-order-total';

export type CustomerSort = 'total-desc' | 'total-asc' | 'name-asc' | 'name-desc' | 'legacy-asc';

export const CUSTOMER_SORTS: readonly { readonly id: CustomerSort; readonly label: string }[] = [
  { id: 'total-desc', label: 'Highest spending first' },
  { id: 'total-asc', label: 'Lowest spending first' },
  { id: 'name-asc', label: 'Name, A to Z' },
  { id: 'name-desc', label: 'Name, Z to A' },
  { id: 'legacy-asc', label: 'Legacy customer ID' },
];

export const PAGE_SIZES = [25, 50, 100] as const;
export type PageSize = (typeof PAGE_SIZES)[number];

export interface CustomerFilters {
  /** Matched against the name and the legacy customer ID, ignoring case. */
  readonly search: string;
  readonly hideZero: boolean;
  readonly sort: CustomerSort;
  readonly pageSize: PageSize;
}

export interface CustomerPage {
  /** Customers in the response, before filtering. */
  readonly totalCount: number;
  /** Customers left after the search and the zero-spending filter. */
  readonly matchCount: number;
  readonly zeroCount: number;
  readonly rows: readonly CustomerOrderTotal[];
  /** Zero-based, and always within range. */
  readonly pageIndex: number;
  readonly pageCount: number;
  readonly firstRow: number;
  readonly lastRow: number;
}

const byName = (a: CustomerOrderTotal, b: CustomerOrderTotal): number =>
  a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }) ||
  a.legacyCustomerId.localeCompare(b.legacyCustomerId);

const COMPARERS: Record<CustomerSort, (a: CustomerOrderTotal, b: CustomerOrderTotal) => number> = {
  'total-desc': (a, b) => b.total - a.total || byName(a, b),
  'total-asc': (a, b) => a.total - b.total || byName(a, b),
  'name-asc': byName,
  'name-desc': (a, b) => byName(b, a),
  'legacy-asc': (a, b) => a.legacyCustomerId.localeCompare(b.legacyCustomerId),
};

/**
 * Filters, sorts and pages the rows in the browser. GET /api/customers/totals
 * returns every customer in one response and has no paging, sorting or search
 * parameters, so this is the only place it can happen.
 */
export function pageCustomers(
  rows: readonly CustomerOrderTotal[],
  filters: CustomerFilters,
  requestedPage: number,
): CustomerPage {
  const needle = filters.search.trim().toLowerCase();

  const matches = rows
    .filter((row) => !filters.hideZero || row.total > 0)
    .filter(
      (row) =>
        needle === '' ||
        row.name.toLowerCase().includes(needle) ||
        row.legacyCustomerId.toLowerCase().includes(needle),
    )
    .sort(COMPARERS[filters.sort]);

  const pageCount = Math.max(1, Math.ceil(matches.length / filters.pageSize));
  const pageIndex = Math.min(Math.max(requestedPage, 0), pageCount - 1);
  const start = pageIndex * filters.pageSize;
  const pageRows = matches.slice(start, start + filters.pageSize);

  return {
    totalCount: rows.length,
    matchCount: matches.length,
    zeroCount: rows.filter((row) => row.total === 0).length,
    rows: pageRows,
    pageIndex,
    pageCount,
    firstRow: pageRows.length === 0 ? 0 : start + 1,
    lastRow: start + pageRows.length,
  };
}
