import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterModule } from '@angular/router';

import { OrdersModule } from '../../orders.module';
import { OrdersPageComponent } from './orders-page.component';

describe('OrdersPageComponent', () => {
  let fixture: ComponentFixture<OrdersPageComponent>;
  let navigate: jest.SpyInstance;

  const query = <T extends HTMLElement = HTMLElement>(cy: string): T =>
    fixture.nativeElement.querySelector(`[data-cy="${cy}"]`) as T;

  const lookUp = (value: string): void => {
    const input = query<HTMLInputElement>('order-lookup-id');
    input.value = value;
    input.dispatchEvent(new Event('input'));
    query<HTMLFormElement>('order-lookup-form').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [OrdersModule, RouterModule.forRoot([])] });
    navigate = jest.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(OrdersPageComponent);
    fixture.detectChanges();
  });

  it('opens the order with the number entered', () => {
    lookUp('42');

    expect(navigate).toHaveBeenCalledWith(['/orders', 42]);
  });

  it.each([
    ['', 'This field is required.'],
    ['0', 'Enter a number from 1 to 2,147,483,647.'],
    ['2.5', 'Enter a whole number.'],
  ])('rejects %p with "%s"', (value, message) => {
    lookUp(value);

    expect(navigate).not.toHaveBeenCalled();
    expect(query('order-lookup-error').textContent?.trim()).toBe(message);
  });

  it('links to the new order form', () => {
    expect(query('orders-new').getAttribute('href')).toBe('/orders/new');
  });
});
