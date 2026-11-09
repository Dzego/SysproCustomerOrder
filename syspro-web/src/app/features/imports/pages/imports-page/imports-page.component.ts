import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-imports-page',
  templateUrl: './imports-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ImportsPageComponent {}
