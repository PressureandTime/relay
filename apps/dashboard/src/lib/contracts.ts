export interface Endpoint {
  id: string;
  name: string;
  url: string;
  createdAtUtc?: string;
}

export interface Receiver {
  id: string;
  url: string;
}

export interface ReceiverResponse extends Receiver {
  signingSecret: string;
}

export interface DeliverySummary {
  id: string;
  eventId?: string;
  endpointId?: string;
  endpointName?: string;
  eventType?: string;
  state: string;
  correlationId?: string;
  errorCode?: string;
  errorMessage?: string;
  attemptCount?: number;
  maxAttempts?: number;
  nextAttemptAtUtc?: string;
  replayOfDeliveryId?: string;
  createdAtUtc?: string;
  startedAtUtc?: string;
  completedAtUtc?: string;
}

export interface DeliveryAttempt {
  id: string;
  number: number;
  state?: string;
  statusCode?: number;
  startedAtUtc?: string;
  completedAtUtc?: string;
  durationMilliseconds?: number;
  error?: string;
  responseBody?: string;
}

export interface DeliveryDetail extends DeliverySummary {
  attempts: DeliveryAttempt[];
}

export interface EventAcceptedResponse {
  eventId: string;
  deliveryId: string;
  state: string;
  correlationId: string;
}

export interface ReplayAcceptedResponse {
  originalDeliveryId: string;
  deliveryId: string;
  state: string;
  correlationId: string;
}

export interface LoadResult<T> {
  data: T[];
  error?: string;
}

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return undefined;
  }

  return value as JsonRecord;
}

function stringValue(
  value: JsonRecord,
  ...keys: string[]
): string | undefined {
  for (const key of keys) {
    const candidate = value[key];
    if (typeof candidate === "string" && candidate.length > 0) {
      return candidate;
    }
  }

  return undefined;
}

function numberValue(
  value: JsonRecord,
  ...keys: string[]
): number | undefined {
  for (const key of keys) {
    const candidate = value[key];
    if (typeof candidate === "number" && Number.isFinite(candidate)) {
      return candidate;
    }
  }

  return undefined;
}

function printableValue(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }

  if (value === undefined || value === null) {
    return undefined;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function collectionFrom(value: unknown, keys: string[]): unknown[] {
  if (Array.isArray(value)) {
    return value;
  }

  const record = asRecord(value);
  if (!record) {
    return [];
  }

  for (const key of keys) {
    if (Array.isArray(record[key])) {
      return record[key];
    }
  }

  return [];
}

export function normalizeEndpoint(value: unknown): Endpoint | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  const id = stringValue(record, "id", "endpointId");
  const name = stringValue(record, "name");
  const url = stringValue(record, "url");
  if (!id || !name || !url) {
    return undefined;
  }

  return {
    id,
    name,
    url,
    createdAtUtc: stringValue(record, "createdAtUtc", "createdAt"),
  };
}

export function normalizeEndpoints(value: unknown): Endpoint[] {
  return collectionFrom(value, ["items", "endpoints"])
    .map(normalizeEndpoint)
    .filter((endpoint): endpoint is Endpoint => endpoint !== undefined);
}

export function normalizeDeliverySummary(
  value: unknown,
): DeliverySummary | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  const id = stringValue(record, "id", "deliveryId");
  if (!id) {
    return undefined;
  }

  return {
    id,
    eventId: stringValue(record, "eventId"),
    endpointId: stringValue(record, "endpointId"),
    endpointName: stringValue(record, "endpointName"),
    eventType: stringValue(record, "eventType", "type"),
    state: stringValue(record, "state", "status") ?? "Unknown",
    correlationId: stringValue(record, "correlationId"),
    errorCode: stringValue(record, "errorCode"),
    errorMessage: stringValue(record, "errorMessage"),
    attemptCount: numberValue(record, "attemptCount"),
    maxAttempts: numberValue(record, "maxAttempts"),
    nextAttemptAtUtc: stringValue(record, "nextAttemptAtUtc"),
    replayOfDeliveryId: stringValue(record, "replayOfDeliveryId"),
    createdAtUtc: stringValue(record, "createdAtUtc", "createdAt"),
    startedAtUtc: stringValue(record, "startedAtUtc", "startedAt"),
    completedAtUtc: stringValue(record, "completedAtUtc", "completedAt"),
  };
}

export function normalizeDeliveries(value: unknown): DeliverySummary[] {
  return collectionFrom(value, ["items", "deliveries"])
    .map(normalizeDeliverySummary)
    .filter((delivery): delivery is DeliverySummary => delivery !== undefined);
}

function normalizeAttempt(
  value: unknown,
  index: number,
  deliveryId: string,
): DeliveryAttempt | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  const number = numberValue(record, "number", "attemptNumber") ?? index + 1;

  return {
    id: stringValue(record, "id", "attemptId") ?? `${deliveryId}-${number}`,
    number,
    state: stringValue(record, "state", "status"),
    statusCode: numberValue(record, "statusCode", "httpStatusCode"),
    startedAtUtc: stringValue(record, "startedAtUtc", "startedAt"),
    completedAtUtc: stringValue(record, "completedAtUtc", "completedAt"),
    durationMilliseconds: numberValue(record, "durationMilliseconds"),
    error: printableValue(record.error ?? record.errorMessage),
    responseBody: printableValue(record.responseBody ?? record.response),
  };
}

export function normalizeDeliveryDetail(
  value: unknown,
): DeliveryDetail | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  const nestedDelivery = asRecord(record.delivery);
  const summary = normalizeDeliverySummary(nestedDelivery ?? record);
  if (!summary) {
    return undefined;
  }

  const rawAttempts = Array.isArray(record.attempts) ? record.attempts : [];
  const attempts = rawAttempts
    .map((attempt, index) => normalizeAttempt(attempt, index, summary.id))
    .filter((attempt): attempt is DeliveryAttempt => attempt !== undefined);

  return { ...summary, attempts };
}

export function apiErrorMessage(value: unknown): string | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  return stringValue(record, "detail", "message", "title", "error");
}
