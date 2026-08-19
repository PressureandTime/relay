export interface EventSubmissionIntent {
  requestBody: string;
  idempotencyKey: string;
}

export function resolveEventSubmissionIntent(
  requestBody: string,
  currentIntent: EventSubmissionIntent | null,
  createIdempotencyKey: () => string = () => crypto.randomUUID(),
): EventSubmissionIntent {
  if (currentIntent?.requestBody === requestBody) {
    return currentIntent;
  }

  return {
    requestBody,
    idempotencyKey: createIdempotencyKey(),
  };
}
