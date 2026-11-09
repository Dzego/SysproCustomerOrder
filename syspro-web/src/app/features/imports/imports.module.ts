import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { ImportsRoutingModule } from './imports-routing.module';
import { ImportsPageComponent } from './pages/imports-page/imports-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /imports route. */
@NgModule({
  declarations: [ImportsPageComponent],
  imports: [SharedModule, ImportsRoutingModule],
})
export class ImportsModule {}
