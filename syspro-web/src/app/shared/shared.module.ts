import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';

import { DateRangeFilterComponent } from './components/date-range-filter/date-range-filter.component';
import { EmptyStateComponent } from './components/empty-state/empty-state.component';
import { ErrorStateComponent } from './components/error-state/error-state.component';
import { LoadingStateComponent } from './components/loading-state/loading-state.component';
import { PageHeaderComponent } from './components/page-header/page-header.component';

const COMPONENTS = [
  PageHeaderComponent,
  LoadingStateComponent,
  EmptyStateComponent,
  ErrorStateComponent,
  DateRangeFilterComponent,
];

/**
 * Reusable, stateless building blocks (components, pipes, directives).
 *
 * Every feature module imports SharedModule, so it must not provide services:
 * each lazy-loaded module that imports it would get its own instance.
 * Application-wide services live in CoreModule or use providedIn: 'root'.
 *
 * It re-exports CommonModule and ReactiveFormsModule, so a feature module gets
 * the common directives, pipes and form directives from one import.
 */
@NgModule({
  declarations: COMPONENTS,
  imports: [CommonModule, ReactiveFormsModule],
  exports: [CommonModule, ReactiveFormsModule, ...COMPONENTS],
})
export class SharedModule {}
