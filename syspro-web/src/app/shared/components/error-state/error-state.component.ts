import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

/**
 * Shown when a request failed. role="alert" makes screen readers announce it
 * as soon as it appears. It receives plain values rather than an ApiError, so
 * SharedModule does not depend on the API layer in CoreModule.
 */
@Component({
  selector: 'app-error-state',
  templateUrl: './error-state.component.html',
  styleUrl: './error-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ErrorStateComponent {
  @Input() heading = 'Something went wrong';
  @Input({ required: true }) message!: string;
  /** Shown so the user can quote it when reporting the problem. */
  @Input() traceId: string | null = null;
  /** Hides the retry button for errors that repeating the request cannot fix. */
  @Input() retryable = true;
  @Input() retryLabel = 'Try again';

  @Output() readonly retry = new EventEmitter<void>();
}
