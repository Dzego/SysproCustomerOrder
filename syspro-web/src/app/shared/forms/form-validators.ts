import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** C# int.MaxValue, the largest id or quantity the API accepts. */
export const MAX_INT = 2_147_483_647;

/** Required, and not only spaces: the API's NotEmpty rejects whitespace too. */
export const requiredText: ValidatorFn = (control: AbstractControl<string>) =>
  control.value.trim() === '' ? { required: true } : null;

/** Length after trimming, which is what is sent. */
export function trimmedMaxLength(max: number): ValidatorFn {
  return (control: AbstractControl<string>) => {
    const length = control.value.trim().length;

    return length > max ? { maxlength: { requiredLength: max, actualLength: length } } : null;
  };
}

/** A whole number in [min, max]. Empty is left to Validators.required. */
export function wholeNumber(min: number, max: number = MAX_INT): ValidatorFn {
  return (control: AbstractControl<number | null>) => {
    const value = control.value;

    if (value === null) {
      return null;
    }

    if (!Number.isInteger(value)) {
      return { wholeNumber: true };
    }

    return value < min || value > max ? { range: { min, max } } : null;
  };
}

/**
 * The message for the errors every form shares, or null if none of them is
 * present. A `server` error is a message from the API and always comes first.
 */
export function commonErrorMessage(errors: ValidationErrors | null): string | null {
  if (!errors) {
    return null;
  }

  if (errors['server']) {
    return errors['server'] as string;
  }

  if (errors['required']) {
    return 'This field is required.';
  }

  if (errors['maxlength']) {
    return `Use at most ${(errors['maxlength'] as { requiredLength: number }).requiredLength} characters.`;
  }

  if (errors['wholeNumber']) {
    return 'Enter a whole number.';
  }

  if (errors['range']) {
    const { min, max } = errors['range'] as { min: number; max: number };

    return `Enter a number from ${min.toLocaleString('en-US')} to ${max.toLocaleString('en-US')}.`;
  }

  return null;
}
