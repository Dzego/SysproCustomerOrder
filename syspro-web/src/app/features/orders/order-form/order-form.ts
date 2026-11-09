import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';

import { CreateOrderRequest } from '../../../core/api/models/order';
import {
  MAX_INT,
  commonErrorMessage,
  requiredText,
  trimmedMaxLength,
  wholeNumber,
} from '../../../shared/forms/form-validators';
import { toCents } from '../../../shared/money/money';

/**
 * Limits that mirror CreateOrderRequestValidator, CreateOrderItemRequestValidator
 * and the column sizes in AppDbContext, so most mistakes are caught before a
 * request is sent. The API remains the authority: it validates every request.
 */
export const ORDER_LIMITS = {
  legacyCustomerIdMaxLength: 10,
  statusMaxLength: 30,
  skuMaxLength: 50,
  descriptionMaxLength: 200,
  maxInt: MAX_INT,
  /**
   * The API accepts decimal(18,2), up to 16 whole digits. A JavaScript number
   * holds about 15 significant digits exactly, so the form stops below that.
   */
  maxUnitPrice: 999_999_999_999.99,
} as const;

export type CustomerMode = 'id' | 'legacy';

export interface OrderItemForm {
  sku: FormControl<string>;
  description: FormControl<string>;
  quantity: FormControl<number | null>;
  unitPrice: FormControl<number | null>;
}

export interface CreateOrderForm {
  customerMode: FormControl<CustomerMode>;
  customerId: FormControl<number | null>;
  legacyCustomerId: FormControl<string>;
  currency: FormControl<string>;
  status: FormControl<string>;
  items: FormArray<FormGroup<OrderItemForm>>;
}

/** Three letters, as the API's ^[A-Za-z]{3}\z pattern requires. */
export const currencyCode: ValidatorFn = (control: AbstractControl<string>) =>
  control.value.trim() === '' || /^[A-Za-z]{3}$/.test(control.value.trim())
    ? null
    : { currencyCode: true };

/** Zero or more, at most two decimals, and no larger than ORDER_LIMITS.maxUnitPrice. */
export const unitPrice: ValidatorFn = (control: AbstractControl<number | null>) => {
  const value = control.value;

  if (value === null) {
    return null;
  }

  if (value < 0) {
    return { min: { min: 0 } };
  }

  if (toCents(value) === null) {
    return { decimals: true };
  }

  return value > ORDER_LIMITS.maxUnitPrice ? { max: { max: ORDER_LIMITS.maxUnitPrice } } : null;
};

export function createItemGroup(): FormGroup<OrderItemForm> {
  return new FormGroup<OrderItemForm>({
    sku: new FormControl('', {
      nonNullable: true,
      validators: [requiredText, trimmedMaxLength(ORDER_LIMITS.skuMaxLength)],
    }),
    // The API only requires that a description is present, so it may be empty.
    description: new FormControl('', {
      nonNullable: true,
      validators: [trimmedMaxLength(ORDER_LIMITS.descriptionMaxLength)],
    }),
    quantity: new FormControl<number | null>(1, {
      validators: [Validators.required, wholeNumber(1)],
    }),
    unitPrice: new FormControl<number | null>(null, {
      validators: [Validators.required, unitPrice],
    }),
  });
}

/** At least one line, as the API requires. */
export const atLeastOneItem: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  (control as FormArray).length === 0 ? { required: true } : null;

export function createOrderForm(): FormGroup<CreateOrderForm> {
  return new FormGroup<CreateOrderForm>({
    customerMode: new FormControl<CustomerMode>('legacy', { nonNullable: true }),
    customerId: new FormControl<number | null>(
      { value: null, disabled: true },
      { validators: [Validators.required, wholeNumber(1)] },
    ),
    legacyCustomerId: new FormControl('', {
      nonNullable: true,
      validators: [requiredText, trimmedMaxLength(ORDER_LIMITS.legacyCustomerIdMaxLength)],
    }),
    currency: new FormControl('ZAR', {
      nonNullable: true,
      validators: [requiredText, currencyCode],
    }),
    status: new FormControl('Pending', {
      nonNullable: true,
      validators: [requiredText, trimmedMaxLength(ORDER_LIMITS.statusMaxLength)],
    }),
    items: new FormArray([createItemGroup()], { validators: [atLeastOneItem] }),
  });
}

/**
 * Enables the control for the chosen way of identifying the customer and
 * disables the other. A disabled control is skipped by validation and left out
 * of the form value, so only one identifier is ever checked and sent.
 */
export function applyCustomerMode(form: FormGroup<CreateOrderForm>, mode: CustomerMode): void {
  const { customerId, legacyCustomerId } = form.controls;
  const [active, inactive] =
    mode === 'id' ? [customerId, legacyCustomerId] : [legacyCustomerId, customerId];

  active.enable({ emitEvent: false });
  inactive.disable({ emitEvent: false });
}

/** The request body for a valid form. Text is trimmed and the currency upper-cased. */
export function toCreateOrderRequest(form: FormGroup<CreateOrderForm>): CreateOrderRequest {
  const value = form.getRawValue();

  return {
    ...(value.customerMode === 'id'
      ? { customerId: value.customerId }
      : { legacyCustomerId: value.legacyCustomerId.trim() }),
    currency: value.currency.trim().toUpperCase(),
    status: value.status.trim(),
    items: value.items.map((item) => ({
      sku: item.sku.trim(),
      description: item.description.trim(),
      // Validators guarantee both are present for a valid form.
      quantity: item.quantity ?? 0,
      unitPrice: item.unitPrice ?? 0,
    })),
  };
}

/** The first message for a control's errors, in the order a user should fix them. */
export function errorMessage(errors: ValidationErrors | null): string | null {
  if (!errors) {
    return null;
  }

  const common = commonErrorMessage(errors);

  if (common) {
    return common;
  }

  if (errors['currencyCode']) {
    return 'Enter a three-letter currency code, such as ZAR.';
  }

  if (errors['min']) {
    return 'The price cannot be negative.';
  }

  if (errors['decimals']) {
    return 'Use at most two decimal places.';
  }

  if (errors['max']) {
    return `The price can be at most ${ORDER_LIMITS.maxUnitPrice.toLocaleString('en-US')}.`;
  }

  return 'Check this value.';
}

/**
 * Puts the API's field errors on the matching controls, as a `server` error that
 * the next edit of that control clears. Returns the messages that match no
 * control, which the page lists in its error summary.
 */
export function applyServerErrors(
  form: FormGroup<CreateOrderForm>,
  errors: ReadonlyMap<string, readonly string[]>,
): string[] {
  const unmatched: string[] = [];

  for (const [path, messages] of errors) {
    const control = findControl(form, path);
    const message = messages.join(' ');

    if (control && control.enabled) {
      control.setErrors({ ...(control.errors ?? {}), server: message });
      control.markAsTouched();
    } else {
      unmatched.push(message);
    }
  }

  return unmatched;
}

const ITEM_PATH = /^items\[(\d+)\]\.(sku|description|quantity|unitprice)$/;

const ITEM_FIELDS: Record<string, keyof OrderItemForm> = {
  sku: 'sku',
  description: 'description',
  quantity: 'quantity',
  unitprice: 'unitPrice',
};

function findControl(form: FormGroup<CreateOrderForm>, path: string): AbstractControl | null {
  const { controls } = form;

  switch (path) {
    case 'customerid':
      return controls.customerId;
    case 'legacycustomerid':
      return controls.legacyCustomerId;
    case 'currency':
      return controls.currency;
    case 'status':
      return controls.status;
    case 'items':
      return controls.items;
  }

  const item = ITEM_PATH.exec(path);

  if (!item) {
    return null;
  }

  const group = controls.items.at(Number(item[1]));

  return group ? group.controls[ITEM_FIELDS[item[2]]] : null;
}
