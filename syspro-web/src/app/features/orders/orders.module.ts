import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { OrdersRoutingModule } from './orders-routing.module';
import { OrdersPageComponent } from './pages/orders-page/orders-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /orders route. */
@NgModule({
  declarations: [OrdersPageComponent],
  imports: [SharedModule, OrdersRoutingModule],
})
export class OrdersModule {}
