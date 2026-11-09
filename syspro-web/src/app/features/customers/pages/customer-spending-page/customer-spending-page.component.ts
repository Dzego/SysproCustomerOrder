import { ChangeDetectionStrategy, Component, DestroyRef, Inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup } from '@angular/forms';
import {
  BehaviorSubject,
  Observable,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  map,
  merge,
  startWith,
  switchMap,
} from 'rxjs';

import { isRetryable } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { LoadState, toLoadState } from '../../../../core/api/load-state';
import { CustomerOrderTotal } from '../../../../core/api/models/customer-order-total';
import { CLOCK, Clock } from '../../../../core/time/clock';
import { DateRange, presetRange, toIsoDate } from '../../../../shared/date-range/date-range';
import { formatAmount } from '../../../../shared/money/money';
import {
  CUSTOMER_SORTS,
  CustomerFilters,
  CustomerPage,
  CustomerSort,
  PAGE_SIZES,
  PageSize,
  pageCustomers,
} from '../../customer-spending';

export interface CustomerSpendingViewModel {
  readonly range: DateRange;
  readonly state: LoadState<readonly CustomerOrderTotal[]>;
  readonly filters: CustomerFilters;
  /** Present once the request succeeded. */
  readonly page: CustomerPage | null;
}

interface FilterForm {
  search: FormControl<string>;
  hideZero: FormControl<boolean>;
  sort: FormControl<CustomerSort>;
  pageSize: FormControl<PageSize>;
}

/**
 * Spending per customer for a period, from GET /api/customers/totals, with
 * search, sorting and paging in the browser.
 */
@Component({
  selector: 'app-customer-spending-page',
  templateUrl: './customer-spending-page.component.html',
  styleUrl: './customer-spending-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class CustomerSpendingPageComponent {
  readonly today: string;
  readonly sorts = CUSTOMER_SORTS;
  readonly pageSizes = PAGE_SIZES;
  readonly isRetryable = isRetryable;
  readonly formatAmount = formatAmount;

  readonly filterForm = new FormGroup<FilterForm>({
    search: new FormControl('', { nonNullable: true }),
    hideZero: new FormControl(false, { nonNullable: true }),
    sort: new FormControl<CustomerSort>('total-desc', { nonNullable: true }),
    pageSize: new FormControl<PageSize>(25, { nonNullable: true }),
  });

  /** Private: only the filter and Retry change it. The same pattern as the dashboard. */
  private readonly appliedRange: BehaviorSubject<DateRange>;
  private readonly pageIndex = new BehaviorSubject(0);

  readonly vm$: Observable<CustomerSpendingViewModel>;

  constructor(
    customersApi: CustomersApiService,
    @Inject(CLOCK) clock: Clock,
    destroyRef: DestroyRef,
  ) {
    this.today = toIsoDate(clock());
    this.appliedRange = new BehaviorSubject(presetRange('last30', this.today));

    // switchMap: a new range cancels the request for the previous one, so an
    // older, slower response never replaces newer results.
    const request$ = this.appliedRange.pipe(
      switchMap((range) =>
        customersApi.getCustomerTotals(range).pipe(
          toLoadState(),
          map((state) => ({ range, state })),
        ),
      ),
    );

    const { search, hideZero, sort, pageSize } = this.filterForm.controls;

    // Typing filters rows that are already loaded, with no request. debounceTime
    // still avoids re-sorting thousands of rows on every keystroke, and
    // distinctUntilChanged skips edits that leave the same text (such as adding
    // and removing a space at the end).
    const search$ = search.valueChanges.pipe(
      map((value) => value.trim()),
      debounceTime(250),
      distinctUntilChanged(),
      startWith(''),
    );

    const filters$: Observable<CustomerFilters> = combineLatest([
      search$,
      hideZero.valueChanges.pipe(startWith(hideZero.value)),
      sort.valueChanges.pipe(startWith(sort.value)),
      pageSize.valueChanges.pipe(startWith(pageSize.value)),
    ]).pipe(
      map(([searchText, hide, sortBy, size]) => ({
        search: searchText,
        hideZero: hide,
        sort: sortBy,
        pageSize: size,
      })),
    );

    // Any change to what is shown starts again on the first page.
    merge(filters$, this.appliedRange)
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe(() => this.pageIndex.next(0));

    this.vm$ = combineLatest([request$, filters$, this.pageIndex]).pipe(
      map(([{ range, state }, filters, page]) => ({
        range,
        state,
        filters,
        page: state.status === 'success' ? pageCustomers(state.data, filters, page) : null,
      })),
    );
  }

  onRangeChange(range: DateRange): void {
    this.appliedRange.next(range);
  }

  reload(): void {
    this.appliedRange.next(this.appliedRange.value);
  }

  goToPage(index: number): void {
    this.pageIndex.next(index);
  }

  clearFilters(): void {
    this.filterForm.patchValue({ search: '', hideZero: false });
  }
}
