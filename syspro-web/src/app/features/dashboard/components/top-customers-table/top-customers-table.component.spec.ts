import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RankedCustomer } from '../../dashboard-summary';
import { TopCustomersTableComponent } from './top-customers-table.component';

const customers: RankedCustomer[] = [
  {
    rank: 1,
    customerId: 4,
    legacyCustomerId: '0000012346',
    name: 'Acme',
    total: 1234.5,
    share: 0.6,
  },
  {
    rank: 2,
    customerId: 9,
    legacyCustomerId: '0000012399',
    name: 'Globex',
    total: 823,
    share: 0.4,
  },
];

describe('TopCustomersTableComponent', () => {
  let fixture: ComponentFixture<TopCustomersTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [TopCustomersTableComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(TopCustomersTableComponent);
    fixture.componentRef.setInput('customers', customers);
    fixture.componentRef.setInput('caption', 'Top customers in the period');
    fixture.detectChanges();
  });

  it('renders one row per customer with formatted totals and shares', () => {
    // Arrange
    const rows: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('[data-cy="top-customer-row"]'),
    );

    // Assert
    expect(rows).toHaveLength(2);
    expect(rows[0].querySelector('[data-cy="top-customer-name"]')?.textContent).toBe('Acme');
    expect(rows[0].textContent).toContain('0000012346');
    expect(rows[0].querySelector('[data-cy="top-customer-total"]')?.textContent).toBe('1,234.50');
    expect(rows[0].querySelector('[data-cy="top-customer-share"]')?.textContent).toBe('60%');
  });

  it('names the table and its scrollable region for assistive technology', () => {
    // Assert
    const region: HTMLElement = fixture.nativeElement.querySelector('[role="region"]');
    const caption: HTMLElement = fixture.nativeElement.querySelector('caption');
    expect(region.getAttribute('aria-label')).toBe('Top customers in the period');
    expect(region.tabIndex).toBe(0);
    expect(caption.textContent?.trim()).toBe('Top customers in the period');
  });
});
