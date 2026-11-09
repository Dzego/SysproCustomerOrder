import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { CustomersRoutingModule } from './customers-routing.module';
import { CustomerSpendingPageComponent } from './pages/customer-spending-page/customer-spending-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /customers route. */
@NgModule({
  declarations: [CustomerSpendingPageComponent],
  imports: [SharedModule, CustomersRoutingModule],
})
export class CustomersModule {}
