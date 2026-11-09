import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { MAX_INT, commonErrorMessage, wholeNumber } from '../../../../shared/forms/form-validators';

/**
 * Entry point for orders: look one up by number, or start a new one. The API
 * has no endpoint that lists or searches orders, so lookup is by number only.
 */
@Component({
  selector: 'app-orders-page',
  templateUrl: './orders-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class OrdersPageComponent {
  readonly form = new FormGroup({
    orderId: new FormControl<number | null>(null, {
      validators: [Validators.required, wholeNumber(1)],
    }),
  });

  readonly maxId = MAX_INT;

  submitted = false;

  constructor(private readonly router: Router) {}

  get orderIdError(): string | null {
    return this.submitted ? commonErrorMessage(this.form.controls.orderId.errors) : null;
  }

  lookUp(): void {
    this.submitted = true;

    const { orderId } = this.form.controls;

    if (orderId.invalid || orderId.value === null) {
      return;
    }

    void this.router.navigate(['/orders', orderId.value]);
  }
}
