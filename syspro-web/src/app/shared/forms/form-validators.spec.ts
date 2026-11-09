import { FormControl } from '@angular/forms';

import {
  MAX_INT,
  commonErrorMessage,
  requiredText,
  trimmedMaxLength,
  wholeNumber,
} from './form-validators';

describe('form validators', () => {
  it('requiredText rejects empty and whitespace-only text', () => {
    expect(requiredText(new FormControl('', { nonNullable: true }))).toEqual({ required: true });
    expect(requiredText(new FormControl('   ', { nonNullable: true }))).toEqual({ required: true });
    expect(requiredText(new FormControl(' a ', { nonNullable: true }))).toBeNull();
  });

  it('trimmedMaxLength measures the text that will be sent', () => {
    const validator = trimmedMaxLength(3);

    expect(validator(new FormControl('  abc  ', { nonNullable: true }))).toBeNull();
    expect(validator(new FormControl('abcd', { nonNullable: true }))).toEqual({
      maxlength: { requiredLength: 3, actualLength: 4 },
    });
  });

  it.each([
    [null, null],
    [1, null],
    [MAX_INT, null],
    [0, { range: { min: 1, max: MAX_INT } }],
    [MAX_INT + 1, { range: { min: 1, max: MAX_INT } }],
    [1.5, { wholeNumber: true }],
  ])('wholeNumber(1) for %p gives %p', (value, expected) => {
    expect(wholeNumber(1)(new FormControl<number | null>(value))).toEqual(expected);
  });

  it('gives the server message first, then the common messages', () => {
    expect(commonErrorMessage({ required: true, server: 'From the API.' })).toBe('From the API.');
    expect(commonErrorMessage({ required: true })).toBe('This field is required.');
    expect(commonErrorMessage({ maxlength: { requiredLength: 10 } })).toBe(
      'Use at most 10 characters.',
    );
    expect(commonErrorMessage({ wholeNumber: true })).toBe('Enter a whole number.');
    expect(commonErrorMessage({ range: { min: 1, max: 2000 } })).toBe(
      'Enter a number from 1 to 2,000.',
    );
    expect(commonErrorMessage({ other: true })).toBeNull();
    expect(commonErrorMessage(null)).toBeNull();
  });
});
