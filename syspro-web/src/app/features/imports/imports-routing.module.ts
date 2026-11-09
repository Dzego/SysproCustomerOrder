import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

import { ImportsPageComponent } from './pages/imports-page/imports-page.component';

const routes: Routes = [{ path: '', component: ImportsPageComponent, title: 'Customer imports' }];

// forChild registers routes without creating a second Router instance.
@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule],
})
export class ImportsRoutingModule {}
