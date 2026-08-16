import { describe, expect, it } from "vitest";
import {
  appendUniqueDeliveries,
  deliveryHistoryPath,
  deliveryMatchesFilters,
  hasDeliveryFilters,
  normalizeDeliveryFilters,
} from "./delivery-filters";

describe("delivery history filters", () => {
  it("builds an encoded query for active filters", () => {
    expect(
      deliveryHistoryPath({
        state: "Succeeded",
        endpointId: "019fbf1d-493a-7d4d-ac06-02fbed992609",
        eventType: "file.processed",
      }),
    ).toBe(
      "/relay-api/deliveries?limit=20&state=Succeeded&endpointId=019fbf1d-493a-7d4d-ac06-02fbed992609&eventType=file.processed",
    );
  });

  it("adds an opaque continuation cursor", () => {
    expect(
      deliveryHistoryPath(
        { state: "", endpointId: "", eventType: "" },
        { cursor: "cursor/value+with=characters", limit: 10 },
      ),
    ).toBe(
      "/relay-api/deliveries?limit=10&cursor=cursor%2Fvalue%2Bwith%3Dcharacters",
    );
  });

  it("normalizes input and detects active filters", () => {
    const filters = normalizeDeliveryFilters({
      state: "Failed",
      endpointId: "",
      eventType: "  file.failed  ",
    });

    expect(filters).toEqual({
      state: "Failed",
      endpointId: "",
      eventType: "file.failed",
    });
    expect(hasDeliveryFilters(filters)).toBe(true);
  });

  it("matches all active filters", () => {
    const delivery = {
      id: "019fbf1d-5c7d-72ca-81c9-9264dc785b61",
      endpointId: "019fbf1d-493a-7d4d-ac06-02fbed992609",
      eventType: "file.processed",
      state: "Succeeded",
    };

    expect(
      deliveryMatchesFilters(delivery, {
        state: "succeeded",
        endpointId: delivery.endpointId,
        eventType: delivery.eventType,
      }),
    ).toBe(true);
    expect(
      deliveryMatchesFilters(delivery, {
        state: "Failed",
        endpointId: "",
        eventType: "",
      }),
    ).toBe(false);
  });

  it("appends pages without duplicating delivery IDs", () => {
    const first = { id: "delivery-1", state: "Succeeded" };
    const second = { id: "delivery-2", state: "Queued" };
    const third = { id: "delivery-3", state: "Failed" };

    expect(appendUniqueDeliveries([first, second], [second, third]))
      .toEqual([first, second, third]);
  });
});
