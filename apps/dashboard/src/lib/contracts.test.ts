import { describe, expect, it } from "vitest";
import {
  apiErrorMessage,
  isEndpointActive,
  normalizeDeliveries,
  normalizeDeliveryDetail,
  normalizeDeliveryHistoryPage,
  normalizeEndpoint,
} from "./contracts";

describe("dashboard API contract normalization", () => {
  it("normalizes endpoint responses without retaining a signing secret", () => {
    expect(
      normalizeEndpoint({
        id: "019fbf1d-493a-7d4d-ac06-02fbed992609",
        name: "Synthetic receiver",
        url: "http://receiver:8080/webhooks/019fbf1d-493a-7d4d-ac06-02fbed992608",
        state: "Disabled",
        createdAtUtc: "2026-08-01T20:57:00Z",
        signingSecret: "must-not-be-mapped",
      }),
    ).toEqual({
      id: "019fbf1d-493a-7d4d-ac06-02fbed992609",
      name: "Synthetic receiver",
      url: "http://receiver:8080/webhooks/019fbf1d-493a-7d4d-ac06-02fbed992608",
      state: "Disabled",
      createdAtUtc: "2026-08-01T20:57:00Z",
    });
  });

  it("treats legacy endpoint responses as active", () => {
    const endpoint = normalizeEndpoint({
        id: "019fbf1d-493a-7d4d-ac06-02fbed992609",
        name: "Synthetic receiver",
        url: "http://receiver:8080/webhooks/019fbf1d-493a-7d4d-ac06-02fbed992608",
      });

    expect(endpoint).toMatchObject({ state: "Active" });
    expect(isEndpointActive(endpoint!)).toBe(true);
    expect(isEndpointActive({ ...endpoint!, state: "Disabled" })).toBe(false);
  });

  it("drops malformed endpoint and delivery records", () => {
    expect(normalizeEndpoint({ name: "Missing fields" })).toBeUndefined();
    expect(
      normalizeDeliveries([
        null,
        { state: "Queued" },
        {
          id: "019fbf1d-5c7d-72ca-81c9-9264dc785b61",
          eventType: "file.processed",
          state: "Queued",
        },
      ]),
    ).toEqual([
      {
        id: "019fbf1d-5c7d-72ca-81c9-9264dc785b61",
        eventType: "file.processed",
        state: "Queued",
      },
    ]);
  });

  it("maps persisted delivery and attempt fields returned by the API", () => {
    expect(
      normalizeDeliveryDetail({
        id: "019fbf1d-6c8b-77de-8608-ef64120636d7",
        eventId: "019fbf1d-6c8b-77de-8608-ef64120636d6",
        endpointId: "019fbf1d-6c8b-77de-8608-ef64120636d5",
        endpointName: "Synthetic receiver",
        eventType: "file.processed",
        state: "Succeeded",
        correlationId: "synthetic-correlation",
        createdAtUtc: "2026-08-01T20:57:00Z",
        startedAtUtc: "2026-08-01T20:57:01Z",
        completedAtUtc: "2026-08-01T20:57:02Z",
        attempts: [
          {
            id: "019fbf1d-6c8b-77de-8608-ef64120636d8",
            attemptNumber: 1,
            state: "Succeeded",
            httpStatusCode: 204,
            startedAtUtc: "2026-08-01T20:57:01Z",
            completedAtUtc: "2026-08-01T20:57:02Z",
            durationMilliseconds: 18,
          },
        ],
        attemptCount: 1,
        maxAttempts: 4,
        nextAttemptAtUtc: undefined,
        replayOfDeliveryId: undefined,
      }),
    ).toMatchObject({
      id: "019fbf1d-6c8b-77de-8608-ef64120636d7",
      endpointName: "Synthetic receiver",
      state: "Succeeded",
      completedAtUtc: "2026-08-01T20:57:02Z",
      attemptCount: 1,
      maxAttempts: 4,
      attempts: [
        {
          number: 1,
          state: "Succeeded",
          statusCode: 204,
          durationMilliseconds: 18,
        },
      ],
    });
  });

  it("normalizes delivery history page metadata", () => {
    expect(
      normalizeDeliveryHistoryPage({
        items: [
          {
            id: "019fbf1d-5c7d-72ca-81c9-9264dc785b61",
            state: "Queued",
          },
        ],
        nextCursor: "cursor-value",
      }),
    ).toEqual({
      items: [
        {
          id: "019fbf1d-5c7d-72ca-81c9-9264dc785b61",
          state: "Queued",
        },
      ],
      nextCursor: "cursor-value",
    });

    expect(normalizeDeliveryHistoryPage({ items: [], nextCursor: null }))
      .toEqual({ items: [], nextCursor: undefined });
    expect(normalizeDeliveryHistoryPage({ nextCursor: "missing-items" }))
      .toBeUndefined();
    expect(normalizeDeliveryHistoryPage([])).toBeUndefined();
    expect(normalizeDeliveryHistoryPage({ items: [], nextCursor: 42 }))
      .toBeUndefined();
    expect(normalizeDeliveryHistoryPage({ items: [] })).toBeUndefined();
  });

  it("selects a useful Problem Details message", () => {
    expect(
      apiErrorMessage({
        title: "Validation failed.",
        detail: "The synthetic payload is invalid.",
      }),
    ).toBe("The synthetic payload is invalid.");
    expect(apiErrorMessage([])).toBeUndefined();
  });
});
