import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import { TrackedJob } from '../../import-jobs.store';
import { STATUS_TONES } from '../../import-job-view';

/** Presentational: the jobs this tab is following, newest first. */
@Component({
  selector: 'app-import-job-list',
  templateUrl: './import-job-list.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ImportJobListComponent {
  @Input({ required: true }) jobs!: readonly TrackedJob[];
  @Input() selectedId: number | null = null;

  @Output() readonly selectJob = new EventEmitter<number>();
  @Output() readonly forgetJob = new EventEmitter<number>();

  readonly tones = STATUS_TONES;
}
