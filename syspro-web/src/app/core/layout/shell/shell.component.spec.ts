import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterModule } from '@angular/router';

import { APP_CONFIG, AppConfig } from '../../config/app-config';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { TopbarComponent } from '../topbar/topbar.component';
import { ShellComponent } from './shell.component';

@Component({
  template: '<p>routed page</p>',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
class StubPageComponent {}

const testConfig: AppConfig = {
  production: false,
  environmentName: 'Test',
  apiBaseUrl: '/api',
  importPollIntervalMs: 1000,
};

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let router: Router;

  const query = <T extends Element = HTMLElement>(cy: string): T | null =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`);

  const toggleButton = (): HTMLButtonElement => query<HTMLButtonElement>('nav-toggle')!;

  const openNav = (): void => {
    toggleButton().click();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ShellComponent, SidebarComponent, TopbarComponent, StubPageComponent],
      imports: [
        RouterModule.forRoot([
          { path: 'first', component: StubPageComponent },
          { path: 'second', component: StubPageComponent },
        ]),
      ],
      providers: [{ provide: APP_CONFIG, useValue: testConfig }],
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(ShellComponent);
    fixture.autoDetectChanges();

    // The initial page load: the shell deliberately ignores this navigation.
    await router.navigateByUrl('/first');
  });

  it('renders the routed page inside the main landmark', () => {
    // Assert
    expect(query('main-content')?.tagName).toBe('MAIN');
    expect(query('main-content')?.textContent).toContain('routed page');
  });

  it('opens the navigation drawer from the menu button and reports the state to assistive technology', () => {
    // Arrange
    expect(toggleButton().getAttribute('aria-expanded')).toBe('false');

    // Act
    openNav();

    // Assert
    expect(toggleButton().getAttribute('aria-expanded')).toBe('true');
    expect(toggleButton().getAttribute('aria-label')).toBe('Close navigation');
    expect(query('nav-backdrop')).not.toBeNull();
  });

  it('closes the drawer when the backdrop is clicked', () => {
    // Arrange
    openNav();

    // Act
    query('nav-backdrop')!.click();
    fixture.detectChanges();

    // Assert
    expect(toggleButton().getAttribute('aria-expanded')).toBe('false');
    expect(query('nav-backdrop')).toBeNull();
  });

  it('closes the drawer when Escape is pressed', () => {
    // Arrange
    openNav();

    // Act
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    // Assert
    expect(toggleButton().getAttribute('aria-expanded')).toBe('false');
  });

  it('closes the drawer and moves focus to the new page after a navigation', async () => {
    // Arrange
    openNav();

    // Act
    await router.navigateByUrl('/second');
    fixture.detectChanges();

    // Assert
    expect(toggleButton().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(query('main-content'));
  });

  it('does not steal focus on the initial page load', () => {
    // Assert: beforeEach performed the first navigation.
    expect(document.activeElement).not.toBe(query('main-content'));
  });

  it('moves focus to the main content from the skip link without changing the URL', () => {
    // Arrange
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });

    // Act
    query('skip-link')!.dispatchEvent(event);

    // Assert
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(query('main-content'));
    expect(router.url).toBe('/first');
  });

  it('stops reacting to navigation once destroyed', async () => {
    // Arrange
    openNav();
    const component = fixture.componentInstance;
    fixture.destroy();

    // Act
    await router.navigateByUrl('/second');

    // Assert: the subscription was released, so the destroyed shell kept its state.
    expect(component.navOpen).toBe(true);
  });
});
