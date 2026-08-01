import {
  type DeliverySummary,
  type Endpoint,
  type LoadResult,
  apiErrorMessage,
  normalizeDeliveries,
  normalizeEndpoints,
} from "./contracts";

const apiBaseUrl = (process.env.RELAY_API_BASE_URL ?? "http://api:8080").replace(
  /\/$/,
  "",
);

async function loadCollection<T>(
  path: string,
  label: string,
  normalize: (value: unknown) => T[],
): Promise<LoadResult<T>> {
  try {
    const response = await fetch(`${apiBaseUrl}/api/${path}`, {
      cache: "no-store",
      headers: { Accept: "application/json" },
    });
    const body: unknown = await response.json().catch(() => undefined);

    if (!response.ok) {
      throw new Error(
        apiErrorMessage(body) ?? `${label} request failed (${response.status}).`,
      );
    }

    return { data: normalize(body) };
  } catch (error) {
    const detail = error instanceof Error ? error.message : "Unknown error";
    return { data: [], error: `Could not load ${label}: ${detail}` };
  }
}

export function loadEndpoints(): Promise<LoadResult<Endpoint>> {
  return loadCollection("endpoints", "endpoints", normalizeEndpoints);
}

export function loadRecentDeliveries(): Promise<LoadResult<DeliverySummary>> {
  return loadCollection(
    "deliveries?limit=20",
    "recent deliveries",
    normalizeDeliveries,
  );
}
