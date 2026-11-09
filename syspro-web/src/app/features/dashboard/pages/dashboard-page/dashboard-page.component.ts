import { ChangeDetectionStrategy, Component, Inject } from '@angular/core';
import { BehaviorSubject, Observable, map, switchMap } from 'rxjs';

import { isRetryable } from '../../../../core/api/api-error';
import { CustomersApiService } from '../../../../core/api/customers-api.service';
import { LoadState, toLoadState } from '../../../../core/api/load-state';
import { CLOCK, Clock } from '../../../../core/time/clock';
import { DateRange, presetRange, toIsoDate } from '../../../../shared/date-range/date-range';
import { DashboardSummary, summarizeCustomerTotals } from '../../dashboard-summary';

export interface DashboardViewModel {
  /** The range the shown (or loading) data belongs to. */
  readonly range: DateRange;
  readonly state: LoadState<DashboardSummary>;
}

/**
 * Customer spending for a period, from GET /api/customers/totals.
 *
 * State flow: user action -> appliedRange -> switchMap(request) -> vm$ -> async pipe.
 */
@Component({
  selector: 'app-dashboard-page',
  templateUrl: './dashboard-page.component.html',
  styleUrl: './dashboard-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class DashboardPageComponent {
  /** Today's UTC date, read once, so the presets stay stable while the page is open. */
  readonly today: string;

  readonly isRetryable = isRetryable;

  /**
   * The range the user last applied. A BehaviorSubject because the state needs
   * both things it offers:
   * - an initial value, so the first request starts as soon as the template
   *   subscribes, with no separate "initial load" code path;
   * - the current value, synchronously, so Retry and Refresh can request the
   *   same range again by re-emitting it.
   * It is private: only this component's event handlers can push a range, and
   * the template sees the read-only vm$ stream.
   */
  private readonly appliedRange: BehaviorSubject<DateRange>;

  /**
   * Everything the template renders. switchMap maps each applied range to a
   * request and unsubscribes from the previous one, so a slow response for an
   * old range can never overwrite the data for a newer one (HttpClient cancels
   * the abandoned request). toLoadState emits 'loading' first and turns an
   * error into a value inside the inner stream, so after a failure vm$ is
   * still alive and the next range or retry is still requested.
   *
   * The template subscribes once through the async pipe, which also
   * unsubscribes when the page is destroyed, so no shareReplay or manual
   * teardown is needed.
   */
  readonly vm$: Observable<DashboardViewModel>;

  constructor(customersApi: CustomersApiService, @Inject(CLOCK) clock: Clock) {
    this.today = toIsoDate(clock());
    this.appliedRange = new BehaviorSubject(presetRange('last30', this.today));

    this.vm$ = this.appliedRange.pipe(
      switchMap((range) =>
        customersApi.getCustomerTotals(range).pipe(
          map(summarizeCustomerTotals),
          toLoadState(),
          map((state): DashboardViewModel => ({ range, state })),
        ),
      ),
    );
  }

  onRangeChange(range: DateRange): void {
    this.appliedRange.next(range);
  }

  /** Requests the applied range again: Retry after an error, Refresh for new orders. */
  reload(): void {
    this.appliedRange.next(this.appliedRange.value);
  }
}
