import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
} from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';

import {
  DATE_RANGE_PRESETS,
  DateRange,
  DateRangePresetId,
  isIsoDate,
  matchPreset,
  presetRange,
} from '../../date-range/date-range';

type PresetChoice = DateRangePresetId | 'custom';

interface DateRangeForm {
  preset: FormControl<PresetChoice>;
  from: FormControl<string>;
  to: FormControl<string>;
}

const isoDateValidator = (control: AbstractControl<string>): ValidationErrors | null =>
  control.value === '' || isIsoDate(control.value) ? null : { isoDate: true };

/** Mirrors the API rule "fromDate cannot be later than toDate", so the request is never sent. */
const rangeOrderValidator = (group: AbstractControl): ValidationErrors | null => {
  const { from, to } = (group as FormGroup<DateRangeForm>).getRawValue();

  return isIsoDate(from) && isIsoDate(to) && from > to ? { rangeOrder: true } : null;
};

/**
 * Picks an inclusive range of UTC days. Choosing a preset applies it at once.
 * Editing a date switches to "Custom range" and applies on submit, so a
 * half-typed date never sends a request.
 *
 * The parent owns the applied range: this component receives it as an input
 * and reports the user's choice through rangeChange.
 */
@Component({
  selector: 'app-date-range-filter',
  templateUrl: './date-range-filter.component.html',
  styleUrl: './date-range-filter.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class DateRangeFilterComponent implements OnChanges {
  @Input({ required: true }) range!: DateRange;
  /** Today's UTC date, which the presets count back from. */
  @Input({ required: true }) today!: string;
  /** Prefix for element ids, so two filters on one page keep their labels apart. */
  @Input() idPrefix = 'date-range';

  @Output() readonly rangeChange = new EventEmitter<DateRange>();

  readonly presets = DATE_RANGE_PRESETS;

  readonly form = new FormGroup<DateRangeForm>(
    {
      preset: new FormControl<PresetChoice>('custom', { nonNullable: true }),
      from: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, isoDateValidator],
      }),
      to: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, isoDateValidator],
      }),
    },
    { validators: rangeOrderValidator },
  );

  /** Errors are shown after the first submit attempt, not while the user is typing. */
  submitted = false;

  ngOnChanges(): void {
    this.form.setValue({
      preset: matchPreset(this.range, this.today) ?? 'custom',
      from: this.range.from,
      to: this.range.to,
    });
    this.submitted = false;
  }

  onPresetChange(): void {
    const preset = this.form.controls.preset.value;

    if (preset === 'custom') {
      return;
    }

    const range = presetRange(preset, this.today);

    this.form.patchValue(range);
    this.submitted = false;
    this.rangeChange.emit(range);
  }

  onDateEdited(): void {
    this.form.controls.preset.setValue('custom');
  }

  onSubmit(): void {
    this.submitted = true;

    if (this.form.invalid) {
      return;
    }

    const { from, to } = this.form.getRawValue();

    this.rangeChange.emit({ from, to });
  }

  /** The message for one date field, or null when it is valid or not yet submitted. */
  fieldError(field: 'from' | 'to'): string | null {
    const control = this.form.controls[field];

    if (!this.submitted || control.valid) {
      return null;
    }

    return control.hasError('required') ? 'Enter a date.' : 'Enter a valid date.';
  }

  get rangeError(): string | null {
    return this.submitted && this.form.hasError('rangeOrder')
      ? 'The start date must be on or before the end date.'
      : null;
  }
}
