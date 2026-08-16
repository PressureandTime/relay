import type { DeliverySummary } from "./contracts";

export const DELIVERY_STATES = [
  "Queued",
  "Processing",
  "RetryScheduled",
  "Succeeded",
  "Failed",
] as const;

export interface DeliveryFilters {
  state: string;
  endpointId: string;
  eventType: string;
}

export const EMPTY_DELIVERY_FILTERS: DeliveryFilters = {
  state: "",
  endpointId: "",
  eventType: "",
};

export function normalizeDeliveryFilters(
  filters: DeliveryFilters,
): DeliveryFilters {
  return {
    state: filters.state,
    endpointId: filters.endpointId,
    eventType: filters.eventType.trim(),
  };
}

export function hasDeliveryFilters(filters: DeliveryFilters): boolean {
  return Boolean(filters.state || filters.endpointId || filters.eventType);
}

export function deliveryHistoryPath(
  filters: DeliveryFilters,
  limit = 20,
): string {
  const parameters = new URLSearchParams({ limit: String(limit) });
  if (filters.state) parameters.set("state", filters.state);
  if (filters.endpointId) parameters.set("endpointId", filters.endpointId);
  if (filters.eventType) parameters.set("eventType", filters.eventType);
  return `/relay-api/deliveries?${parameters.toString()}`;
}

export function deliveryMatchesFilters(
  delivery: DeliverySummary,
  filters: DeliveryFilters,
): boolean {
  return (
    (!filters.state
      || delivery.state.toLowerCase() === filters.state.toLowerCase())
    && (!filters.endpointId || delivery.endpointId === filters.endpointId)
    && (!filters.eventType || delivery.eventType === filters.eventType)
  );
}
