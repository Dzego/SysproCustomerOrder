import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { EmptyStateComponent } from './empty-state.component';

@Component({
  template: `
    <app-empty-state
      heading="No customers yet"
      message="Import customers first."
      [headingLevel]="3"
    >
      <a href="/imports" data-cy="projected-action">Go to imports</a>
    </app-empty-state>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
class HostComponent {}

describe('EmptyStateComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  const query = (cy: string): HTMLElement | null =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [EmptyStateComponent, HostComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  it('renders the heading at the requested outline level', () => {
    // Assert
    const heading = query('empty-state-heading')!;
    expect(heading.getAttribute('role')).toBe('heading');
    expect(heading.getAttribute('aria-level')).toBe('3');
    expect(heading.textContent?.trim()).toBe('No customers yet');
  });

  it('renders the message and the projected next step', () => {
    // Assert
    expect(query('empty-state-message')?.textContent?.trim()).toBe('Import customers first.');
    expect(query('projected-action')?.textContent?.trim()).toBe('Go to imports');
  });
});
