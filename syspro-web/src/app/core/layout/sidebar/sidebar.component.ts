import { ChangeDetectionStrategy, Component, EventEmitter, Output } from '@angular/core';

import { NAV_ITEMS, NavItem } from '../../navigation/nav-items';

/** Primary navigation. Presentational: it renders links and reports clicks. */
@Component({
  selector: 'app-sidebar',
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class SidebarComponent {
  /** Raised when a link is chosen, so the shell can close the mobile drawer. */
  @Output() readonly navigate = new EventEmitter<void>();

  readonly items: readonly NavItem[] = NAV_ITEMS;
}
