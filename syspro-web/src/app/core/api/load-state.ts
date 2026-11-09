import { OperatorFunction, catchError, map, of, startWith } from 'rxjs';

import { ApiError, toApiError } from './api-error';

/**
 * The three states a page shows for one request. A discriminated union, so the
 * template cannot read `data` without first checking that the status is
 * 'success'; strictTemplates enforces this.
 *
 * "Empty" is not a separate status: whether a successful result counts as empty
 * depends on the page (no customers at all, or no orders in the period).
 */
export type LoadState<T> =
  | { readonly status: 'loading' }
  | { readonly status: 'success'; readonly data: T }
  | { readonly status: 'error'; readonly error: ApiError };

const LOADING: LoadState<never> = { status: 'loading' };

/**
 * Turns one request into a stream of LoadStates: 'loading' straight away, then
 * 'success' or 'error'.
 *
 * catchError converts the failure into a normal value, so the stream completes
 * instead of erroring. Applied to the inner request of a switchMap, this keeps
 * the outer stream alive: after a failure the user can retry, and the next
 * request still runs.
 */
export function toLoadState<T>(): OperatorFunction<T, LoadState<T>> {
  return (source) =>
    source.pipe(
      map((data): LoadState<T> => ({ status: 'success', data })),
      startWith(LOADING),
      catchError((error: unknown) =>
        of<LoadState<T>>({ status: 'error', error: toApiError(error) }),
      ),
    );
}
