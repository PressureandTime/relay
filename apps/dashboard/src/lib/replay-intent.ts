export interface ReplayIntent {
  deliveryId: string;
  idempotencyKey: string;
}

export function resolveReplayIntent(
  deliveryId: string,
  currentIntent: ReplayIntent | null,
  createIdempotencyKey: () => string = () => crypto.randomUUID(),
): ReplayIntent {
  if (currentIntent?.deliveryId === deliveryId) {
    return currentIntent;
  }

  return {
    deliveryId,
    idempotencyKey: createIdempotencyKey(),
  };
}
