import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { DashboardRoutingModule } from './dashboard-routing.module';
import { DashboardPageComponent } from './pages/dashboard-page/dashboard-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /dashboard route. */
@NgModule({
  declarations: [DashboardPageComponent],
  imports: [SharedModule, DashboardRoutingModule],
})
export class DashboardModule {}
