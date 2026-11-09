import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { NgModule, Optional, SkipSelf } from '@angular/core';
import { RouterModule, TitleStrategy } from '@angular/router';

import { environment } from '../../environments/environment';
import { SharedModule } from '../shared/shared.module';
import { APP_CONFIG } from './config/app-config';
import { NotFoundComponent } from './layout/not-found/not-found.component';
import { ShellComponent } from './layout/shell/shell.component';
import { SidebarComponent } from './layout/sidebar/sidebar.component';
import { TopbarComponent } from './layout/topbar/topbar.component';
import { AppTitleStrategy } from './navigation/app-title-strategy';

/**
 * Application-wide singletons: the layout shell, HTTP setup and configuration.
 * Imported once, by AppModule. Feature modules never import it, so they cannot
 * create second instances of its services.
 */
@NgModule({
  declarations: [ShellComponent, SidebarComponent, TopbarComponent, NotFoundComponent],
  imports: [RouterModule, SharedModule],
  exports: [ShellComponent],
  providers: [
    // withInterceptorsFromDi() enables class-based interceptors registered with
    // the HTTP_INTERCEPTORS token, the NgModule-style approach (added in Phase 4).
    provideHttpClient(withInterceptorsFromDi()),
    { provide: APP_CONFIG, useValue: environment },
    { provide: TitleStrategy, useClass: AppTitleStrategy },
  ],
})
export class CoreModule {
  constructor(@Optional() @SkipSelf() parentModule: CoreModule | null) {
    if (parentModule) {
      throw new Error('CoreModule is already loaded. Import it in AppModule only.');
    }
  }
}
