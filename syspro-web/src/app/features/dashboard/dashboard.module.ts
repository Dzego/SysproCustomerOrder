import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { TopCustomersTableComponent } from './components/top-customers-table/top-customers-table.component';
import { DashboardRoutingModule } from './dashboard-routing.module';
import { DashboardPageComponent } from './pages/dashboard-page/dashboard-page.component';

/**
 * Lazy-loaded feature module, loaded by AppRoutingModule on the /dashboard route.
 * DashboardRoutingModule re-exports RouterModule, which provides routerLink.
 */
@NgModule({
  declarations: [DashboardPageComponent, TopCustomersTableComponent],
  imports: [SharedModule, DashboardRoutingModule],
})
export class DashboardModule {}
