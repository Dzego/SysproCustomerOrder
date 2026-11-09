import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Root component. It only hosts the shell; the layout lives in CoreModule. */
@Component({
  selector: 'app-root',
  template: '<app-shell />',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class AppComponent {}
