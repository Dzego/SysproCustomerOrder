import { HttpErrorResponse } from '@angular/common/http';
import { MonoTypeOperatorFunction, catchError, throwError } from 'rxjs';

import { isProblemDetails } from './models/problem-details';

/**
 * What went wrong, from the user's point of view. Pages choose wording and
 * actions (retry, fix the form) from the kind, not from raw status codes.
 */
export type ApiErrorKind =
  /** The request never got a response: offline, DNS failure, or the request was blocked. */
  | 'network'
  /** A gateway or proxy answered for the API: it is down or restarting (502, 503, 504). */
  | 'unavailable'
  /** 400 with field errors (ValidationProblemDetails). */
  | 'validation'
  /** 404. */
  | 'not-found'
  /** Any other 4xx: the API rejected the request, and its detail explains why. */
  | 'rejected'
  /** 5xx from the API itself. */
  | 'server'
  /** Not an HTTP failure, for example a bug in code that handled the response. */
  | 'unexpected';

/**
 * The single error type that API services emit. Components never inspect
 * HttpErrorResponse, so the mapping rules live in one place.
 */
export class ApiError extends Error {
  constructor(
    readonly kind: ApiErrorKind,
    /** Message that is safe to show to the user. */
    message: string,
    readonly status: number | null = null,
    /** The API's traceId, which support can match against the server logs. */
    readonly traceId: string | null = null,
    readonly fieldErrors: Readonly<Record<string, readonly string[]>> = {},
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export const API_ERROR_MESSAGES = {
  network: 'The server could not be reached. Check your connection and try again.',
  unavailable: 'The service is temporarily unavailable. Try again in a moment.',
  validation: 'Some of the values were not accepted.',
  notFound: 'The requested item was not found.',
  rejected: 'The request was not accepted.',
  server: 'The server could not complete the request.',
  unexpected: 'Something went wrong while showing this information.',
} as const;

/** Converts any thrown value into an ApiError. An ApiError is returned unchanged. */
export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) {
    return error;
  }

  if (!(error instanceof HttpErrorResponse)) {
    return new ApiError('unexpected', API_ERROR_MESSAGES.unexpected);
  }

  const { status } = error;

  if (status === 0) {
    return new ApiError('network', API_ERROR_MESSAGES.network, null);
  }

  const problem = isProblemDetails(error.error) ? error.error : null;
  const traceId = problem?.traceId ?? null;
  const detail = problem?.detail?.trim() || null;

  if (status === 502 || status === 503 || status === 504) {
    return new ApiError('unavailable', API_ERROR_MESSAGES.unavailable, status, traceId);
  }

  if (status >= 500) {
    // Never show a 5xx detail: in Development the API puts the full exception,
    // stack trace included, in it. The traceId is enough to find it in the logs.
    return new ApiError('server', API_ERROR_MESSAGES.server, status, traceId);
  }

  if (status === 400 && problem?.errors) {
    return new ApiError(
      'validation',
      detail ?? API_ERROR_MESSAGES.validation,
      status,
      traceId,
      problem.errors,
    );
  }

  if (status === 404) {
    return new ApiError('not-found', detail ?? API_ERROR_MESSAGES.notFound, status, traceId);
  }

  return new ApiError('rejected', detail ?? API_ERROR_MESSAGES.rejected, status, traceId);
}

/**
 * Whether sending the same request again could succeed. A rejected, invalid or
 * not-found request fails the same way until the input changes.
 */
export function isRetryable(error: ApiError): boolean {
  return (
    error.kind === 'network' ||
    error.kind === 'unavailable' ||
    error.kind === 'server' ||
    error.kind === 'unexpected'
  );
}

/** Rethrows any error from an HTTP call as an ApiError. */
export function mapApiError<T>(): MonoTypeOperatorFunction<T> {
  return catchError((error: unknown) => throwError(() => toApiError(error)));
}
