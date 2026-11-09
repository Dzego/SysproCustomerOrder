import { Subject, of, throwError } from 'rxjs';

import { ApiError } from './api-error';
import { LoadState, toLoadState } from './load-state';

const collect = <T>(states: LoadState<T>[]) => ({
  next: (state: LoadState<T>) => states.push(state),
});

describe('toLoadState', () => {
  it('emits loading and then success with the data', () => {
    // Arrange
    const states: LoadState<number>[] = [];

    // Act
    of(42).pipe(toLoadState()).subscribe(collect(states));

    // Assert
    expect(states).toEqual([{ status: 'loading' }, { status: 'success', data: 42 }]);
  });

  it('emits loading before the request answers', () => {
    // Arrange
    const response = new Subject<string>();
    const states: LoadState<string>[] = [];

    // Act
    response.pipe(toLoadState()).subscribe(collect(states));

    // Assert
    expect(states).toEqual([{ status: 'loading' }]);
  });

  it('turns a failure into an error state and completes instead of erroring', () => {
    // Arrange
    const apiError = new ApiError('server', 'Server failed', 500, 'trace-1');
    const states: LoadState<number>[] = [];
    const observer = { ...collect(states), error: jest.fn(), complete: jest.fn() };

    // Act
    throwError(() => apiError)
      .pipe(toLoadState<number>())
      .subscribe(observer);

    // Assert
    expect(states).toEqual([{ status: 'loading' }, { status: 'error', error: apiError }]);
    expect(observer.error).not.toHaveBeenCalled();
    expect(observer.complete).toHaveBeenCalledTimes(1);
  });

  it('converts a non-API failure into an ApiError', () => {
    // Arrange
    const states: LoadState<number>[] = [];

    // Act
    throwError(() => new Error('bug'))
      .pipe(toLoadState<number>())
      .subscribe(collect(states));

    // Assert
    const last = states[states.length - 1];
    expect(last.status).toBe('error');
    expect(last.status === 'error' && last.error.kind).toBe('unexpected');
  });
});
