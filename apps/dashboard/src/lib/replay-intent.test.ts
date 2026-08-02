import { describe, expect, it, vi } from "vitest";
import { resolveReplayIntent } from "./replay-intent";

describe("replay intent", () => {
  it("reuses one idempotency key after an uncertain response or double submit", () => {
    const createKey = vi.fn(() => "replay-key");
    const firstAttempt = resolveReplayIntent("delivery-1", null, createKey);
    const repeatedAttempt = resolveReplayIntent(
      "delivery-1",
      firstAttempt,
      createKey,
    );

    expect(repeatedAttempt).toBe(firstAttempt);
    expect(repeatedAttempt.idempotencyKey).toBe("replay-key");
    expect(createKey).toHaveBeenCalledTimes(1);
  });

  it("creates a new intent when the selected delivery changes", () => {
    const createKey = vi
      .fn<() => string>()
      .mockReturnValueOnce("first-key")
      .mockReturnValueOnce("second-key");
    const firstIntent = resolveReplayIntent("delivery-1", null, createKey);
    const secondIntent = resolveReplayIntent(
      "delivery-2",
      firstIntent,
      createKey,
    );

    expect(secondIntent).toEqual({
      deliveryId: "delivery-2",
      idempotencyKey: "second-key",
    });
    expect(createKey).toHaveBeenCalledTimes(2);
  });
});
