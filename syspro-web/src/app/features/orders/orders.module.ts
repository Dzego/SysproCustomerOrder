import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { OrderSummaryComponent } from './components/order-summary/order-summary.component';
import { OrdersRoutingModule } from './orders-routing.module';
import { CreateOrderPageComponent } from './pages/create-order-page/create-order-page.component';
import { OrderDetailPageComponent } from './pages/order-detail-page/order-detail-page.component';
import { OrdersPageComponent } from './pages/orders-page/orders-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /orders route. */
@NgModule({
  declarations: [
    OrdersPageComponent,
    CreateOrderPageComponent,
    OrderDetailPageComponent,
    OrderSummaryComponent,
  ],
  imports: [SharedModule, OrdersRoutingModule],
})
export class OrdersModule {}
