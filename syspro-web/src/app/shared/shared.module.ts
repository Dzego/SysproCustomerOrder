import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';

import { PageHeaderComponent } from './components/page-header/page-header.component';

/**
 * Reusable, stateless building blocks (components, pipes, directives).
 *
 * Every feature module imports SharedModule, so it must not provide services:
 * each lazy-loaded module that imports it would get its own instance.
 * Application-wide services live in CoreModule or use providedIn: 'root'.
 */
@NgModule({
  declarations: [PageHeaderComponent],
  imports: [CommonModule],
  exports: [CommonModule, PageHeaderComponent],
})
export class SharedModule {}
