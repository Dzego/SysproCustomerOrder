import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

/** Top bar with the mobile menu button. Presentational: the shell owns the state. */
@Component({
  selector: 'app-topbar',
  templateUrl: './topbar.component.html',
  styleUrl: './topbar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class TopbarComponent {
  @Input({ required: true }) navOpen!: boolean;
  @Input({ required: true }) environmentName!: string;

  @Output() readonly menuToggle = new EventEmitter<void>();
}
