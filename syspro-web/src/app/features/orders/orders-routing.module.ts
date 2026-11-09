import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

import { CreateOrderPageComponent } from './pages/create-order-page/create-order-page.component';
import { OrderDetailPageComponent } from './pages/order-detail-page/order-detail-page.component';
import { OrdersPageComponent } from './pages/orders-page/orders-page.component';

// 'new' is listed before ':id', so it is never read as an order number.
const routes: Routes = [
  { path: '', component: OrdersPageComponent, title: 'Orders' },
  { path: 'new', component: CreateOrderPageComponent, title: 'New order' },
  { path: ':id', component: OrderDetailPageComponent, title: 'Order details' },
];

// forChild registers routes without creating a second Router instance.
@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule],
})
export class OrdersRoutingModule {}
