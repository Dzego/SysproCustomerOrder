import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, RouterModule } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { appRoutes } from './app-routing.module';
import { CoreModule } from './core/core.module';

/**
 * Exercises the real route table, including the lazy loadChildren imports, so a
 * broken module path or missing route title fails here rather than in the browser.
 */
describe('AppRoutingModule routes', () => {
  let harness: RouterTestingHarness;

  const pageSelector = (cy: string): HTMLElement | null =>
    harness.routeNativeElement?.querySelector(`[data-cy="${cy}"]`) ?? null;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [CoreModule, RouterModule.forRoot(appRoutes)],
      // The dashboard requests data when it renders. The testing backend keeps
      // that request in memory instead of sending it from jsdom.
      providers: [provideHttpClientTesting()],
    });
    harness = await RouterTestingHarness.create();
  });

  it('redirects the empty path to the dashboard', async () => {
    // Act
    await harness.navigateByUrl('/');

    // Assert
    expect(TestBed.inject(Router).url).toBe('/dashboard');
    expect(pageSelector('dashboard-page')).not.toBeNull();
  });

  it.each([
    ['/dashboard', 'dashboard-page', 'Dashboard'],
    ['/customers', 'customers-page', 'Customer spending'],
    ['/orders', 'orders-page', 'Orders'],
    ['/imports', 'imports-page', 'Customer imports'],
  ])('lazy-loads %s and sets its document title', async (url, page, heading) => {
    // Act
    await harness.navigateByUrl(url);

    // Assert
    expect(pageSelector(page)).not.toBeNull();
    expect(pageSelector('page-heading')?.textContent?.trim()).toBe(heading);
    expect(TestBed.inject(Title).getTitle()).toBe(`${heading} · SYSPRO Orders`);
  });

  it('shows the not-found page for an unknown URL', async () => {
    // Act
    await harness.navigateByUrl('/does-not-exist');

    // Assert
    expect(pageSelector('not-found')).not.toBeNull();
    expect(TestBed.inject(Title).getTitle()).toBe('Page not found · SYSPRO Orders');
  });
});
