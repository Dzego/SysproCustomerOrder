import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-customer-spending-page',
  templateUrl: './customer-spending-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class CustomerSpendingPageComponent {}
