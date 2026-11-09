import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

import { OrdersPageComponent } from './pages/orders-page/orders-page.component';

const routes: Routes = [{ path: '', component: OrdersPageComponent, title: 'Orders' }];

// forChild registers routes without creating a second Router instance.
@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule],
})
export class OrdersRoutingModule {}
