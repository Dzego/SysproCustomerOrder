import { FormControl } from '@angular/forms';

import { normalizeFieldErrors } from '../../../core/api/validation-errors';
import {
  applyCustomerMode,
  applyServerErrors,
  createItemGroup,
  createOrderForm,
  currencyCode,
  errorMessage,
  toCreateOrderRequest,
  unitPrice,
} from './order-form';

/** A form that passes every client-side rule. */
function validForm() {
  const form = createOrderForm();
  form.controls.legacyCustomerId.setValue(' 0000012345 ');
  form.controls.currency.setValue('zar');
  form.controls.status.setValue(' Pending ');
  form.controls.items
    .at(0)
    .setValue({ sku: ' SKU-1 ', description: ' Widget ', quantity: 3, unitPrice: 19.99 });

  return form;
}

describe('order form', () => {
  it('starts with one empty line, legacy-id mode and sensible defaults', () => {
    // Act
    const form = createOrderForm();

    // Assert
    expect(form.controls.items.length).toBe(1);
    expect(form.controls.customerMode.value).toBe('legacy');
    expect(form.controls.customerId.disabled).toBe(true);
    expect(form.controls.currency.value).toBe('ZAR');
    expect(form.controls.status.value).toBe('Pending');
    expect(form.valid).toBe(false);
  });

  it('builds the request from a valid form, trimmed and with an upper-case currency', () => {
    // Arrange
    const form = validForm();

    // Act
    const request = toCreateOrderRequest(form);

    // Assert
    expect(form.valid).toBe(true);
    expect(request).toEqual({
      legacyCustomerId: '0000012345',
      currency: 'ZAR',
      status: 'Pending',
      items: [{ sku: 'SKU-1', description: 'Widget', quantity: 3, unitPrice: 19.99 }],
    });
  });

  it('sends only the identifier for the chosen mode', () => {
    // Arrange
    const form = validForm();

    // Act
    applyCustomerMode(form, 'id');
    form.controls.customerMode.setValue('id');
    form.controls.customerId.setValue(42);

    // Assert
    expect(form.controls.legacyCustomerId.disabled).toBe(true);
    expect(toCreateOrderRequest(form)).toMatchObject({ customerId: 42 });
    expect(toCreateOrderRequest(form)).not.toHaveProperty('legacyCustomerId');
  });

  it('validates the customer number only in number mode', () => {
    const form = validForm();
    applyCustomerMode(form, 'id');

    form.controls.customerId.setValue(null);
    expect(form.controls.customerId.errors).toEqual({ required: true });

    form.controls.customerId.setValue(0);
    expect(form.controls.customerId.hasError('range')).toBe(true);

    applyCustomerMode(form, 'legacy');
    expect(form.valid).toBe(true);
  });

  it('requires a non-blank legacy id of at most 10 characters', () => {
    const control = createOrderForm().controls.legacyCustomerId;

    control.setValue('   ');
    expect(control.hasError('required')).toBe(true);

    control.setValue('12345678901');
    expect(control.hasError('maxlength')).toBe(true);
  });

  it.each([
    ['ZAR', null],
    ['usd', null],
    ['', null],
    ['ZA', { currencyCode: true }],
    ['ZAR1', { currencyCode: true }],
    ['Z4R', { currencyCode: true }],
  ])('currency %p gives %p', (value, expected) => {
    expect(currencyCode(new FormControl(value, { nonNullable: true }))).toEqual(expected);
  });

  it.each([
    [0, null],
    [19.99, null],
    [999_999_999_999.99, null],
    [-0.01, { min: { min: 0 } }],
    [1.005, { decimals: true }],
    [1_000_000_000_000, { max: { max: 999_999_999_999.99 } }],
  ])('unit price %p gives %p', (value, expected) => {
    expect(unitPrice(new FormControl<number | null>(value))).toEqual(expected);
  });

  it('validates each line: SKU, description length, quantity and price', () => {
    // Arrange
    const item = createItemGroup();

    // Act
    item.setValue({ sku: ' ', description: 'x'.repeat(201), quantity: 0, unitPrice: null });

    // Assert
    expect(item.controls.sku.hasError('required')).toBe(true);
    expect(item.controls.description.hasError('maxlength')).toBe(true);
    expect(item.controls.quantity.hasError('range')).toBe(true);
    expect(item.controls.unitPrice.hasError('required')).toBe(true);
  });

  it('allows an empty description, as the API does', () => {
    const item = createItemGroup();
    item.setValue({ sku: 'A', description: '', quantity: 1, unitPrice: 0 });

    expect(item.valid).toBe(true);
  });

  it('rejects an order with no lines', () => {
    const form = validForm();
    form.controls.items.removeAt(0);

    expect(form.controls.items.hasError('required')).toBe(true);
    expect(form.valid).toBe(false);
  });

  describe('server errors', () => {
    it('places API field errors on the matching controls, including FormArray lines', () => {
      // Arrange
      const form = validForm();
      form.controls.items.push(createItemGroup());
      const errors = normalizeFieldErrors({
        Currency: ['Currency must be a 3-letter ISO 4217 code.'],
        'Items[1].UnitPrice': ['Too many decimals.'],
        '$.items[1].quantity': ['Bad number.'],
      });

      // Act
      const unmatched = applyServerErrors(form, errors);

      // Assert
      expect(unmatched).toEqual([]);
      expect(form.controls.currency.errors).toMatchObject({
        server: 'Currency must be a 3-letter ISO 4217 code.',
      });
      expect(form.controls.items.at(1).controls.unitPrice.errors).toMatchObject({
        server: 'Too many decimals.',
      });
      expect(form.controls.items.at(1).controls.quantity.errors).toMatchObject({
        server: 'Bad number.',
      });
    });

    it('returns messages that match no enabled control', () => {
      const form = validForm();

      const unmatched = applyServerErrors(
        form,
        normalizeFieldErrors({
          request: ['Unreadable body.'],
          'Items[5].Sku': ['Gone.'],
          CustomerId: ['Disabled.'],
        }),
      );

      expect(unmatched).toEqual(['Unreadable body.', 'Gone.', 'Disabled.']);
    });

    it('clears a server error once the user edits the field', () => {
      const form = validForm();
      applyServerErrors(form, normalizeFieldErrors({ Status: ['Rejected.'] }));

      form.controls.status.setValue('Confirmed');

      expect(form.controls.status.errors).toBeNull();
    });
  });

  it('chooses one message per control, server message first', () => {
    expect(errorMessage({ server: 'API says no', required: true })).toBe('API says no');
    expect(errorMessage({ currencyCode: true })).toContain('three-letter');
    expect(errorMessage({ decimals: true })).toBe('Use at most two decimal places.');
    expect(errorMessage({ min: { min: 0 } })).toBe('The price cannot be negative.');
    expect(errorMessage({ max: { max: 1 } })).toContain('at most');
    expect(errorMessage({ unknown: true })).toBe('Check this value.');
    expect(errorMessage(null)).toBeNull();
  });
});
