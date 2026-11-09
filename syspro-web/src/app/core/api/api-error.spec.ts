import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, throwError } from 'rxjs';

import { API_ERROR_MESSAGES, ApiError, isRetryable, mapApiError, toApiError } from './api-error';

const httpError = (status: number, body: unknown = null): HttpErrorResponse =>
  new HttpErrorResponse({ status, error: body, url: '/api/test' });

const problem = (status: number, extra: Record<string, unknown> = {}) => ({
  type: 'about:blank',
  title: 'Title',
  status,
  instance: '/api/test',
  traceId: '00-abc-def-01',
  ...extra,
});

describe('toApiError', () => {
  it('reports a request that got no response (status 0) as a network error', () => {
    // Act
    const error = toApiError(httpError(0));

    // Assert
    expect(error.kind).toBe('network');
    expect(error.status).toBeNull();
    expect(error.message).toBe(API_ERROR_MESSAGES.network);
  });

  it.each([502, 503, 504])('reports %i from a gateway as unavailable', (status) => {
    // Act
    const error = toApiError(httpError(status, 'Bad gateway'));

    // Assert
    expect(error.kind).toBe('unavailable');
    expect(error.status).toBe(status);
  });

  it('hides the detail of a 500, which can contain a stack trace, but keeps the traceId', () => {
    // Arrange
    const body = problem(500, { detail: 'System.Exception: boom\n   at Syspro.Api...' });

    // Act
    const error = toApiError(httpError(500, body));

    // Assert
    expect(error.kind).toBe('server');
    expect(error.message).toBe(API_ERROR_MESSAGES.server);
    expect(error.message).not.toContain('System.Exception');
    expect(error.traceId).toBe('00-abc-def-01');
  });

  it('shows the API detail of a rejected request, such as a business rule', () => {
    // Arrange
    const body = problem(400, { detail: 'fromDate cannot be later than toDate.' });

    // Act
    const error = toApiError(httpError(400, body));

    // Assert
    expect(error.kind).toBe('rejected');
    expect(error.message).toBe('fromDate cannot be later than toDate.');
    expect(error.status).toBe(400);
  });

  it('keeps the field errors of a validation problem', () => {
    // Arrange
    const body = problem(400, {
      title: 'One or more validation errors occurred.',
      errors: { Currency: ['Currency must be a 3-letter ISO 4217 code.'] },
    });

    // Act
    const error = toApiError(httpError(400, body));

    // Assert
    expect(error.kind).toBe('validation');
    expect(error.message).toBe(API_ERROR_MESSAGES.validation);
    expect(error.fieldErrors).toEqual({
      Currency: ['Currency must be a 3-letter ISO 4217 code.'],
    });
  });

  it('reports 404 as not found, with the API detail', () => {
    // Act
    const error = toApiError(
      httpError(404, problem(404, { detail: 'Order with id 9 was not found.' })),
    );

    // Assert
    expect(error.kind).toBe('not-found');
    expect(error.message).toBe('Order with id 9 was not found.');
  });

  it('falls back to a generic message when a 4xx body is not ProblemDetails', () => {
    // Act
    const error = toApiError(httpError(413, '<html>Too large</html>'));

    // Assert
    expect(error.kind).toBe('rejected');
    expect(error.message).toBe(API_ERROR_MESSAGES.rejected);
    expect(error.traceId).toBeNull();
  });

  it('reports a non-HTTP failure as unexpected without exposing its message', () => {
    // Act
    const error = toApiError(new TypeError("Cannot read properties of undefined (reading 'x')"));

    // Assert
    expect(error.kind).toBe('unexpected');
    expect(error.message).toBe(API_ERROR_MESSAGES.unexpected);
  });

  it('returns an ApiError unchanged', () => {
    // Arrange
    const original = new ApiError('not-found', 'Gone', 404);

    // Act / Assert
    expect(toApiError(original)).toBe(original);
  });
});

describe('isRetryable', () => {
  it.each([
    ['network', true],
    ['unavailable', true],
    ['server', true],
    ['unexpected', true],
    ['validation', false],
    ['rejected', false],
    ['not-found', false],
  ] as const)('%s -> %s', (kind, expected) => {
    expect(isRetryable(new ApiError(kind, 'message'))).toBe(expected);
  });
});

describe('mapApiError', () => {
  it('rethrows an HttpErrorResponse as an ApiError', async () => {
    // Arrange
    const source = throwError(() => httpError(0)).pipe(mapApiError());

    // Act
    const error = await firstValueFrom(source).catch((caught: unknown) => caught);

    // Assert
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe('network');
  });
});
