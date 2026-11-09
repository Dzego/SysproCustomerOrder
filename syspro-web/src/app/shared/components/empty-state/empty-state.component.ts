import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Shown when a request succeeded but there is nothing to display. Pages project
 * an optional next step (a link or button) into the actions slot.
 */
@Component({
  selector: 'app-empty-state',
  template: `
    <div class="empty-state" data-cy="empty-state">
      <div
        class="empty-state__heading"
        role="heading"
        [attr.aria-level]="headingLevel"
        data-cy="empty-state-heading"
      >
        {{ heading }}
      </div>
      @if (message) {
        <p class="empty-state__message" data-cy="empty-state-message">{{ message }}</p>
      }
      <div class="empty-state__actions">
        <ng-content />
      </div>
    </div>
  `,
  styleUrl: './empty-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class EmptyStateComponent {
  @Input({ required: true }) heading!: string;
  @Input() message?: string;
  /** Outline level for the heading, so it fits under the page's existing headings. */
  @Input() headingLevel = 2;
}
