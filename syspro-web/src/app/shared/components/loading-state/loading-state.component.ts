import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Shown while a request is in flight. role="status" is a polite live region,
 * so screen readers announce the label without interrupting the user.
 */
@Component({
  selector: 'app-loading-state',
  template: `
    <div class="loading-state" role="status" data-cy="loading-state">
      <span class="loading-state__spinner" aria-hidden="true"></span>
      <span class="loading-state__label">{{ label }}</span>
    </div>
  `,
  styleUrl: './loading-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class LoadingStateComponent {
  @Input() label = 'Loading…';
}
