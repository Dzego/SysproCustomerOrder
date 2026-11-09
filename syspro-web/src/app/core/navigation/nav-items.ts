export type NavIcon = 'dashboard' | 'customers' | 'orders' | 'imports';

export interface NavItem {
  readonly label: string;
  readonly path: string;
  readonly icon: NavIcon;
  /** Used as the data-cy hook, so tests do not depend on labels or markup. */
  readonly id: string;
}

/** Primary navigation. Each path matches a lazy-loaded route in AppRoutingModule. */
export const NAV_ITEMS: readonly NavItem[] = [
  { id: 'dashboard', label: 'Dashboard', path: '/dashboard', icon: 'dashboard' },
  { id: 'customers', label: 'Customer spending', path: '/customers', icon: 'customers' },
  { id: 'orders', label: 'Orders', path: '/orders', icon: 'orders' },
  { id: 'imports', label: 'Customer imports', path: '/imports', icon: 'imports' },
];
