import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

import { RankedCustomer } from '../../dashboard-summary';

/** Presentational table of the highest-spending customers in the period. */
@Component({
  selector: 'app-top-customers-table',
  templateUrl: './top-customers-table.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class TopCustomersTableComponent {
  @Input({ required: true }) customers!: readonly RankedCustomer[];
  /** Visually hidden caption that names the table for screen readers. */
  @Input({ required: true }) caption!: string;
}
