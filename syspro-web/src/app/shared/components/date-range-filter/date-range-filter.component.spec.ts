import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReactiveFormsModule } from '@angular/forms';

import { DateRange } from '../../date-range/date-range';
import { DateRangeFilterComponent } from './date-range-filter.component';

const TODAY = '2026-11-09';
const LAST_30: DateRange = { from: '2026-10-11', to: TODAY };

describe('DateRangeFilterComponent', () => {
  let fixture: ComponentFixture<DateRangeFilterComponent>;
  let emitted: DateRange[];

  const query = <T extends HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`);

  const preset = () => query<HTMLSelectElement>('date-range-preset');
  const fromInput = () => query<HTMLInputElement>('date-range-from');
  const toInput = () => query<HTMLInputElement>('date-range-to');

  const typeInto = (input: HTMLInputElement, value: string): void => {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const choosePreset = (value: string): void => {
    preset().value = value;
    preset().dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  const submit = (): void => {
    query<HTMLButtonElement>('date-range-apply').click();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [DateRangeFilterComponent],
      imports: [ReactiveFormsModule],
    }).compileComponents();

    fixture = TestBed.createComponent(DateRangeFilterComponent);
    fixture.componentRef.setInput('range', LAST_30);
    fixture.componentRef.setInput('today', TODAY);
    fixture.detectChanges();

    emitted = [];
    fixture.componentInstance.rangeChange.subscribe((range) => emitted.push(range));
  });

  it('shows the applied range and selects the preset that produced it', () => {
    // Assert
    expect(fromInput().value).toBe('2026-10-11');
    expect(toInput().value).toBe(TODAY);
    expect(preset().value).toBe('last30');
  });

  it('labels every control', () => {
    // Assert
    for (const control of [preset(), fromInput(), toInput()]) {
      const label = fixture.nativeElement.querySelector(`label[for="${control.id}"]`);
      expect(label).not.toBeNull();
    }
  });

  it('applies a preset as soon as it is chosen', () => {
    // Act
    choosePreset('last7');

    // Assert
    expect(emitted).toEqual([{ from: '2026-11-03', to: TODAY }]);
    expect(fromInput().value).toBe('2026-11-03');
  });

  it('switches to a custom range when a date is edited, and applies it only on submit', () => {
    // Act
    typeInto(fromInput(), '2026-11-01');

    // Assert
    expect(preset().value).toBe('custom');
    expect(emitted).toEqual([]);

    // Act
    submit();

    // Assert
    expect(emitted).toEqual([{ from: '2026-11-01', to: TODAY }]);
  });

  it('rejects a start date after the end date without emitting', () => {
    // Arrange
    typeInto(fromInput(), '2026-11-20');

    // Act
    submit();

    // Assert
    const error = query('date-range-error');
    expect(emitted).toEqual([]);
    expect(error.textContent?.trim()).toBe('The start date must be on or before the end date.');
    expect(error.getAttribute('role')).toBe('alert');
    expect(fromInput().getAttribute('aria-invalid')).toBe('true');
    expect(fromInput().getAttribute('aria-describedby')).toBe(error.id);
  });

  it('requires both dates', () => {
    // Arrange
    typeInto(toInput(), '');

    // Act
    submit();

    // Assert
    expect(emitted).toEqual([]);
    expect(query('date-range-to-error').textContent?.trim()).toBe('Enter a date.');
    expect(toInput().getAttribute('aria-describedby')).toBe(query('date-range-to-error').id);
  });

  it('does not show errors before the first submit', () => {
    // Act
    typeInto(fromInput(), '2026-11-20');

    // Assert
    expect(query('date-range-error')).toBeNull();
    expect(fromInput().getAttribute('aria-invalid')).toBeNull();
  });

  it('resets the form when the parent applies a different range', () => {
    // Arrange
    typeInto(fromInput(), '2026-11-20');
    submit();

    // Act
    fixture.componentRef.setInput('range', { from: '2026-01-01', to: TODAY });
    fixture.detectChanges();

    // Assert
    expect(preset().value).toBe('yearToDate');
    expect(fromInput().value).toBe('2026-01-01');
    expect(query('date-range-error')).toBeNull();
  });
});
