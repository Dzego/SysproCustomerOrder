import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterModule } from '@angular/router';

import { NAV_ITEMS } from '../../navigation/nav-items';
import { SidebarComponent } from './sidebar.component';

describe('SidebarComponent', () => {
  let fixture: ComponentFixture<SidebarComponent>;
  let router: Router;

  const link = (id: string): HTMLAnchorElement =>
    fixture.nativeElement.querySelector(`[data-cy="nav-${id}"]`);

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [SidebarComponent],
      imports: [RouterModule.forRoot([{ path: '**', children: [] }])],
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(SidebarComponent);
    fixture.autoDetectChanges();
  });

  it('renders one link per navigation item, pointing at its route', () => {
    // Assert
    for (const item of NAV_ITEMS) {
      expect(link(item.id).getAttribute('href')).toBe(item.path);
      expect(link(item.id).textContent?.trim()).toBe(item.label);
    }
  });

  it('labels the navigation landmark', () => {
    // Assert
    const nav: HTMLElement = fixture.nativeElement.querySelector('nav');
    expect(nav.getAttribute('aria-label')).toBe('Primary');
  });

  it('marks only the active route with aria-current="page"', async () => {
    // Act
    await router.navigateByUrl('/orders');
    fixture.detectChanges();

    // Assert
    expect(link('orders').getAttribute('aria-current')).toBe('page');
    expect(link('orders').classList).toContain('sidebar__link--active');
    expect(link('dashboard').hasAttribute('aria-current')).toBe(false);
  });

  it('raises navigate when a link is chosen, so the shell can close the drawer', () => {
    // Arrange
    const navigate = jest.fn();
    fixture.componentInstance.navigate.subscribe(navigate);

    // Act
    link('customers').click();

    // Assert
    expect(navigate).toHaveBeenCalledTimes(1);
  });
});
