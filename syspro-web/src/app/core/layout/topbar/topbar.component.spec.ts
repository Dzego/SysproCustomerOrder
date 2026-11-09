import { ComponentFixture, TestBed } from '@angular/core/testing';

import { TopbarComponent } from './topbar.component';

describe('TopbarComponent', () => {
  let fixture: ComponentFixture<TopbarComponent>;

  const menuButton = (): HTMLButtonElement =>
    fixture.nativeElement.querySelector('[data-cy="nav-toggle"]');

  beforeEach(async () => {
    await TestBed.configureTestingModule({ declarations: [TopbarComponent] }).compileComponents();

    fixture = TestBed.createComponent(TopbarComponent);
    fixture.componentRef.setInput('navOpen', false);
    fixture.componentRef.setInput('environmentName', 'Development');
    fixture.detectChanges();
  });

  it('describes the closed state on the menu button', () => {
    // Assert
    expect(menuButton().getAttribute('aria-expanded')).toBe('false');
    expect(menuButton().getAttribute('aria-label')).toBe('Open navigation');
    expect(menuButton().getAttribute('aria-controls')).toBe('app-sidebar');
  });

  it('describes the open state when navOpen changes', () => {
    // Act
    fixture.componentRef.setInput('navOpen', true);
    fixture.detectChanges();

    // Assert
    expect(menuButton().getAttribute('aria-expanded')).toBe('true');
    expect(menuButton().getAttribute('aria-label')).toBe('Close navigation');
  });

  it('raises menuToggle when the button is clicked, without owning the state itself', () => {
    // Arrange
    const toggled = jest.fn();
    fixture.componentInstance.menuToggle.subscribe(toggled);

    // Act
    menuButton().click();
    fixture.detectChanges();

    // Assert
    expect(toggled).toHaveBeenCalledTimes(1);
    expect(menuButton().getAttribute('aria-expanded')).toBe('false');
  });
});
