import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Page title block. Each routed page renders exactly one, which supplies its
 * single <h1>. Actions (buttons, links) are projected into the right-hand slot.
 */
@Component({
  selector: 'app-page-header',
  templateUrl: './page-header.component.html',
  styleUrl: './page-header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class PageHeaderComponent {
  @Input({ required: true }) heading!: string;
  @Input() description?: string;
}
