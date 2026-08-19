import { describe, expect, it, vi } from "vitest";
import { resolveEventSubmissionIntent } from "./event-submission-intent";

describe("event submission intent", () => {
  it("reuses one idempotency key for an unchanged request", () => {
    const createKey = vi.fn(() => "event-key");
    const firstAttempt = resolveEventSubmissionIntent(
      '{"endpointId":"endpoint-1","type":"file.processed"}',
      null,
      createKey,
    );
    const repeatedAttempt = resolveEventSubmissionIntent(
      firstAttempt.requestBody,
      firstAttempt,
      createKey,
    );

    expect(repeatedAttempt).toBe(firstAttempt);
    expect(repeatedAttempt.idempotencyKey).toBe("event-key");
    expect(createKey).toHaveBeenCalledTimes(1);
  });

  it("creates a new intent when the request changes", () => {
    const createKey = vi
      .fn<() => string>()
      .mockReturnValueOnce("first-key")
      .mockReturnValueOnce("second-key");
    const firstIntent = resolveEventSubmissionIntent(
      '{"endpointId":"endpoint-1","type":"file.processed"}',
      null,
      createKey,
    );
    const secondIntent = resolveEventSubmissionIntent(
      '{"endpointId":"endpoint-1","type":"file.failed"}',
      firstIntent,
      createKey,
    );

    expect(secondIntent).toEqual({
      requestBody: '{"endpointId":"endpoint-1","type":"file.failed"}',
      idempotencyKey: "second-key",
    });
    expect(createKey).toHaveBeenCalledTimes(2);
  });
});
