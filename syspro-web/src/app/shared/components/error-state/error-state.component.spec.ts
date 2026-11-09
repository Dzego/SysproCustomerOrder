import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ErrorStateComponent } from './error-state.component';

describe('ErrorStateComponent', () => {
  let fixture: ComponentFixture<ErrorStateComponent>;

  const query = (cy: string): HTMLElement | null =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ErrorStateComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ErrorStateComponent);
    fixture.componentRef.setInput('message', 'The server could not be reached.');
  });

  it('announces the error as an alert with a default heading', () => {
    // Act
    fixture.detectChanges();

    // Assert
    expect(query('error-state')?.getAttribute('role')).toBe('alert');
    expect(query('error-state-heading')?.textContent?.trim()).toBe('Something went wrong');
    expect(query('error-state-message')?.textContent?.trim()).toBe(
      'The server could not be reached.',
    );
  });

  it('shows the trace reference only when there is one', () => {
    // Act
    fixture.detectChanges();
    const withoutTrace = query('error-state-trace');

    fixture.componentRef.setInput('traceId', '00-abc-def-01');
    fixture.detectChanges();

    // Assert
    expect(withoutTrace).toBeNull();
    expect(query('error-state-trace')?.textContent).toContain('00-abc-def-01');
  });

  it('raises retry when the retry button is clicked', () => {
    // Arrange
    const retried = jest.fn();
    fixture.componentInstance.retry.subscribe(retried);
    fixture.detectChanges();

    // Act
    query('error-state-retry')!.click();

    // Assert
    expect(retried).toHaveBeenCalledTimes(1);
  });

  it('hides the retry button for errors that retrying cannot fix', () => {
    // Act
    fixture.componentRef.setInput('retryable', false);
    fixture.detectChanges();

    // Assert
    expect(query('error-state-retry')).toBeNull();
  });
});
