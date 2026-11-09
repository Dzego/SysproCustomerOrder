import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LoadingStateComponent } from './loading-state.component';

describe('LoadingStateComponent', () => {
  let fixture: ComponentFixture<LoadingStateComponent>;

  const status = (): HTMLElement =>
    fixture.nativeElement.querySelector('[data-cy="loading-state"]');

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [LoadingStateComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(LoadingStateComponent);
  });

  it('is a status live region with a default label', () => {
    // Act
    fixture.detectChanges();

    // Assert
    expect(status().getAttribute('role')).toBe('status');
    expect(status().textContent?.trim()).toBe('Loading…');
  });

  it('shows a custom label', () => {
    // Act
    fixture.componentRef.setInput('label', 'Loading customer spending…');
    fixture.detectChanges();

    // Assert
    expect(status().textContent?.trim()).toBe('Loading customer spending…');
  });
});
