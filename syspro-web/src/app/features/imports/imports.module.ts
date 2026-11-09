import { NgModule } from '@angular/core';

import { SharedModule } from '../../shared/shared.module';
import { ImportJobDetailsComponent } from './components/import-job-details/import-job-details.component';
import { ImportJobListComponent } from './components/import-job-list/import-job-list.component';
import { ImportsRoutingModule } from './imports-routing.module';
import { ImportsPageComponent } from './pages/imports-page/imports-page.component';

/** Lazy-loaded feature module, loaded by AppRoutingModule on the /imports route. */
@NgModule({
  declarations: [ImportsPageComponent, ImportJobDetailsComponent, ImportJobListComponent],
  imports: [SharedModule, ImportsRoutingModule],
})
export class ImportsModule {}
