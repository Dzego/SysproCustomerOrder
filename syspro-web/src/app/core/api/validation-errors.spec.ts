import { normalizeFieldErrors } from './validation-errors';

describe('normalizeFieldErrors', () => {
  it('normalizes FluentValidation paths and JSON binding paths to one form', () => {
    // Arrange
    const errors = {
      Currency: ['Currency must be a 3-letter ISO 4217 code.'],
      'Items[0].Sku': ["'Sku' must not be empty."],
      '$.items[1].quantity': ['The JSON value could not be converted.'],
      request: ['The request field is required.'],
    };

    // Act
    const normalized = normalizeFieldErrors(errors);

    // Assert
    expect([...normalized.entries()]).toEqual([
      ['currency', ['Currency must be a 3-letter ISO 4217 code.']],
      ['items[0].sku', ["'Sku' must not be empty."]],
      ['items[1].quantity', ['The JSON value could not be converted.']],
      ['request', ['The request field is required.']],
    ]);
  });

  it('merges messages whose keys differ only in case or prefix', () => {
    const normalized = normalizeFieldErrors({ Status: ['A'], '$.status': ['B'] });

    expect(normalized.get('status')).toEqual(['A', 'B']);
  });
});
