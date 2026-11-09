import { ChangeDetectionStrategy, Component, DestroyRef, Inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormGroup } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  BehaviorSubject,
  Observable,
  Subject,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  exhaustMap,
  map,
  shareReplay,
  startWith,
} from 'rxjs';

import { ApiError, isRetryable } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { LoadState, toLoadState } from '../../../../core/api/load-state';
import { CustomerOrderTotal } from '../../../../core/api/models/customer-order-total';
import { OrderResponse } from '../../../../core/api/models/order';
import { OrdersApiService } from '../../../../core/api/orders-api.service';
import { normalizeFieldErrors } from '../../../../core/api/validation-errors';
import { CLOCK, Clock } from '../../../../core/time/clock';
import { toIsoDate } from '../../../../shared/date-range/date-range';
import { formatCents, lineTotalCents, sumCents } from '../../../../shared/money/money';
import {
  CreateOrderForm,
  CustomerMode,
  ORDER_LIMITS,
  OrderItemForm,
  applyCustomerMode,
  applyServerErrors,
  createItemGroup,
  createOrderForm,
  errorMessage,
  toCreateOrderRequest,
} from '../../order-form/order-form';

/** The customer an identifier matches in the directory, or why it does not. */
export type CustomerMatch =
  | { readonly status: 'none' }
  | { readonly status: 'found'; readonly customer: CustomerOrderTotal }
  | { readonly status: 'unknown' };

/** What happened to the latest submission. */
export type SubmitState = { readonly status: 'idle' } | LoadState<OrderResponse>;

const MAX_SUGGESTIONS = 8;

/**
 * Creates an order with POST /api/orders.
 *
 * The API accepts no order date: it records the time the order is saved, in UTC.
 */
@Component({
  selector: 'app-create-order-page',
  templateUrl: './create-order-page.component.html',
  styleUrl: './create-order-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class CreateOrderPageComponent {
  readonly form: FormGroup<CreateOrderForm> = createOrderForm();
  readonly limits = ORDER_LIMITS;
  readonly currencies = ['ZAR', 'USD', 'EUR', 'GBP'] as const;
  readonly statuses = ['Pending', 'Confirmed', 'Shipped', 'Completed', 'Cancelled'] as const;
  readonly errorMessage = errorMessage;
  readonly isRetryable = isRetryable;
  readonly noMatch: CustomerMatch = { status: 'none' };

  /** Errors are shown once a control is touched or a submit was attempted. */
  submitAttempted = false;

  /** Server messages that match no field, listed at the top of the form. */
  formErrors: readonly string[] = [];

  /**
   * Every customer the API knows, for the identifier hint and suggestions. The
   * only endpoint that lists customers is the spending report, so it is asked
   * for all dates. shareReplay lets the hint and the suggestions share one
   * request; refCount drops the cache when the page is destroyed, so a later
   * visit sees customers imported in the meantime.
   */
  readonly directory$: Observable<LoadState<ReadonlyMap<string, CustomerOrderTotal>>>;

  readonly customerMatch$: Observable<CustomerMatch>;
  readonly suggestions$: Observable<readonly CustomerOrderTotal[]>;

  /** Each submit pushes the request here. */
  private readonly submissions = new Subject<void>();

  /** Private, so only this component changes it; the template reads submitState$. */
  private readonly submitStateSubject = new BehaviorSubject<SubmitState>({ status: 'idle' });
  readonly submitState$ = this.submitStateSubject.asObservable();
  readonly submitting$ = this.submitState$.pipe(map((state) => state.status === 'loading'));
  readonly submitError$ = this.submitState$.pipe(
    map((state) => (state.status === 'error' ? state.error : null)),
  );

  constructor(
    private readonly ordersApi: OrdersApiService,
    customersApi: CustomersApiService,
    private readonly router: Router,
    route: ActivatedRoute,
    @Inject(CLOCK) clock: Clock,
    destroyRef: DestroyRef,
  ) {
    // "New order" on the customer spending page passes the legacy customer ID.
    const customer = route.snapshot.queryParamMap.get('customer');

    if (customer) {
      this.form.controls.legacyCustomerId.setValue(
        customer.slice(0, ORDER_LIMITS.legacyCustomerIdMaxLength),
      );
    }

    this.directory$ = customersApi
      .getCustomerTotals({ from: '1900-01-01', to: toIsoDate(clock()) })
      .pipe(
        map((rows) => new Map(rows.flatMap((row) => directoryEntries(row)))),
        toLoadState(),
        shareReplay({ bufferSize: 1, refCount: true }),
      );

    // The identifier the user is typing, in the active mode. debounceTime waits
    // for a pause in typing, and distinctUntilChanged skips values that did not
    // change (for example switching mode back and forth with the same text).
    const identifier$ = this.form.valueChanges.pipe(
      startWith(null),
      map(() => this.typedIdentifier()),
      debounceTime(200),
      distinctUntilChanged((a, b) => a.mode === b.mode && a.text === b.text),
    );

    this.customerMatch$ = combineLatest([this.directory$, identifier$]).pipe(
      map(([directory, { mode, text }]): CustomerMatch => {
        if (text === '' || directory.status !== 'success') {
          return { status: 'none' };
        }

        const customer = directory.data.get(directoryKey(mode, text));

        return customer ? { status: 'found', customer } : { status: 'unknown' };
      }),
    );

    this.suggestions$ = combineLatest([this.directory$, identifier$]).pipe(
      map(([directory, { mode, text }]) =>
        directory.status === 'success' && mode === 'legacy' && text !== ''
          ? suggest(directory.data, text)
          : [],
      ),
    );

    // Only one identifier control is enabled at a time. The subscription has a
    // side effect on the form, so it is tied to the page's lifetime.
    this.form.controls.customerMode.valueChanges
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe((mode) => applyCustomerMode(this.form, mode));

    // exhaustMap ignores submits while a request is in flight, so a double click
    // cannot send the order twice from this page. It does not make the API
    // idempotent: a retry after a lost response can still create a second order.
    this.submissions
      .pipe(
        exhaustMap(() =>
          this.ordersApi.createOrder(toCreateOrderRequest(this.form)).pipe(toLoadState()),
        ),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe((state) => this.onSubmitState(state));
  }

  get items(): FormGroup<OrderItemForm>[] {
    return this.form.controls.items.controls;
  }

  /** Each line's total, or null while its price or quantity is not valid. */
  lineTotal(item: FormGroup<OrderItemForm>): string | null {
    const { unitPrice, quantity } = item.controls;

    if (
      unitPrice.invalid ||
      quantity.invalid ||
      unitPrice.value === null ||
      quantity.value === null
    ) {
      return null;
    }

    const cents = lineTotalCents(unitPrice.value, quantity.value);

    return cents === null ? null : formatCents(cents);
  }

  /** The order total, or null until every line has a valid price and quantity. */
  get orderTotal(): string | null {
    const lines = this.items.map(({ controls: { unitPrice, quantity } }) =>
      unitPrice.valid && quantity.valid && unitPrice.value !== null && quantity.value !== null
        ? lineTotalCents(unitPrice.value, quantity.value)
        : null,
    );

    return lines.every((cents): cents is bigint => cents !== null)
      ? formatCents(sumCents(lines))
      : null;
  }

  get currencyLabel(): string {
    return this.form.controls.currency.value.trim().toUpperCase();
  }

  addItem(): void {
    this.form.controls.items.push(createItemGroup());
  }

  removeItem(index: number): void {
    if (this.items.length > 1) {
      this.form.controls.items.removeAt(index);
    }
  }

  chooseSuggestion(customer: CustomerOrderTotal): void {
    this.form.controls.legacyCustomerId.setValue(customer.legacyCustomerId);
  }

  /** The message to show for a control, or null while it should stay quiet. */
  fieldError(control: AbstractControl): string | null {
    if (!control.enabled || (!control.touched && !this.submitAttempted)) {
      return null;
    }

    return errorMessage(control.errors);
  }

  onSubmit(): void {
    this.submitAttempted = true;
    this.formErrors = [];
    this.form.markAllAsTouched();

    if (this.form.invalid) {
      return;
    }

    this.submissions.next();
  }

  private onSubmitState(state: LoadState<OrderResponse>): void {
    this.submitStateSubject.next(state);

    if (state.status === 'success') {
      // The details page reads the order back from the API, which shows it was saved.
      void this.router.navigate(['/orders', state.data.id], { state: { created: true } });
      return;
    }

    if (state.status === 'error') {
      this.showError(state.error);
    }
  }

  private showError(error: ApiError): void {
    if (error.kind === 'validation') {
      this.formErrors = applyServerErrors(this.form, normalizeFieldErrors(error.fieldErrors));
      return;
    }

    if (error.kind === 'not-found') {
      // POST /api/orders answers 404 when no customer has the identifier.
      const mode = this.form.controls.customerMode.value;
      const control =
        mode === 'id' ? this.form.controls.customerId : this.form.controls.legacyCustomerId;

      control.setErrors({ server: error.message });
      control.markAsTouched();
    }
  }

  private typedIdentifier(): { mode: CustomerMode; text: string } {
    const { customerMode, customerId, legacyCustomerId } = this.form.getRawValue();

    return {
      mode: customerMode,
      text:
        customerMode === 'id'
          ? customerId === null
            ? ''
            : String(customerId)
          : legacyCustomerId.trim(),
    };
  }
}

/** Legacy ids compare case-insensitively, as SQL Server's default collation does. */
function directoryKey(mode: CustomerMode, text: string): string {
  return mode === 'id' ? `id:${text}` : `legacy:${text.toUpperCase()}`;
}

function directoryEntries(row: CustomerOrderTotal): [string, CustomerOrderTotal][] {
  return [
    [directoryKey('id', String(row.customerId)), row],
    [directoryKey('legacy', row.legacyCustomerId), row],
  ];
}

function suggest(
  directory: ReadonlyMap<string, CustomerOrderTotal>,
  text: string,
): CustomerOrderTotal[] {
  const needle = text.toLowerCase();
  const matches: CustomerOrderTotal[] = [];

  for (const [key, customer] of directory) {
    if (!key.startsWith('legacy:')) {
      continue;
    }

    if (
      customer.legacyCustomerId.toLowerCase().startsWith(needle) ||
      customer.name.toLowerCase().includes(needle)
    ) {
      matches.push(customer);

      if (matches.length === MAX_SUGGESTIONS) {
        break;
      }
    }
  }

  return matches;
}
