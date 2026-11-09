import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import { isRetryable } from '../../../../core/api/api-error';
import { isTerminalJob } from '../../../../core/api/models/import-job-status';
import { parseUtc } from '../../../../shared/time/utc';
import { TrackedJob } from '../../import-jobs.store';
import { STATUS_TONES, describeJob } from '../../import-job-view';

/** Presentational: everything the API reports about one import job. */
@Component({
  selector: 'app-import-job-details',
  templateUrl: './import-job-details.component.html',
  styleUrl: './import-job-details.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ImportJobDetailsComponent {
  @Input({ required: true }) tracked!: TrackedJob;
  /** Whether a new import can be started from here right now. */
  @Input() canStart = true;

  @Output() readonly resumeUpdates = new EventEmitter<number>();
  @Output() readonly startNew = new EventEmitter<void>();

  readonly tones = STATUS_TONES;
  readonly describeJob = describeJob;
  readonly isTerminalJob = isTerminalJob;
  readonly isRetryable = isRetryable;
  readonly parseUtc = parseUtc;
}
