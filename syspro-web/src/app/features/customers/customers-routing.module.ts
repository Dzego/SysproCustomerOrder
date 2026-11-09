import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

import { CustomerSpendingPageComponent } from './pages/customer-spending-page/customer-spending-page.component';

const routes: Routes = [
  { path: '', component: CustomerSpendingPageComponent, title: 'Customer spending' },
];

// forChild registers routes without creating a second Router instance.
@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule],
})
export class CustomersRoutingModule {}
