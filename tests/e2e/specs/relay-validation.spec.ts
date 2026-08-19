import { expect, test } from "@playwright/test";

test("shows event validation, loading, server error, and recovery states", async ({
  page,
}) => {
  const suffix = crypto.randomUUID().slice(0, 8);
  const endpointName = `Validation receiver ${suffix}`;
  const eventType = `relay.recovery.${suffix}`;

  await page.goto("/");
  await page.getByRole("button", { name: "Prepare receiver" }).click();
  await expect(
    page.getByText("Receiver prepared. Continue to endpoint registration."),
  ).toBeVisible();

  await page.getByRole("textbox", { name: "Endpoint name" }).fill(endpointName);
  await page.getByRole("button", { name: "Register endpoint" }).click();
  await expect(
    page.getByText(new RegExp(`Endpoint .${endpointName}. registered`)),
  ).toBeVisible();

  const payloadInput = page.getByRole("textbox", { name: "JSON payload" });
  const sendButton = page.getByRole("button", { name: "Send event" });
  await page
    .getByRole("textbox", { name: "Event type", exact: true })
    .fill(eventType);
  const idempotencyKeys: string[] = [];
  page.on("request", (request) => {
    if (
      request.method() === "POST"
      && new URL(request.url()).pathname === "/relay-api/events"
    ) {
      idempotencyKeys.push(request.headers()["idempotency-key"] ?? "");
    }
  });

  await payloadInput.fill("{not-json}");
  await sendButton.click();
  await expect(
    page.getByRole("alert").filter({ hasText: "Payload must be valid JSON." }),
  ).toBeVisible();
  expect(idempotencyKeys).toEqual([]);

  await payloadInput.fill(JSON.stringify({ status: "testing" }, null, 2));
  let acceptedSubmission:
    | { eventId: string; deliveryId: string; correlationId: string }
    | undefined;
  let releaseFailureResponse: () => void = () => undefined;
  const failureResponseGate = new Promise<void>((resolve) => {
    releaseFailureResponse = resolve;
  });
  await page.route("**/relay-api/events", async (route) => {
    await failureResponseGate;
    const response = await route.fetch();
    expect(response.status()).toBe(202);
    acceptedSubmission = await response.json();
    await route.fulfill({
      status: 503,
      contentType: "application/problem+json",
      body: JSON.stringify({ title: "Synthetic event failure." }),
    });
  });

  await sendButton.click();
  await expect(page.getByRole("button", { name: "Submitting…" })).toBeDisabled();
  releaseFailureResponse();
  await expect(
    page.getByRole("alert").filter({
      hasText: "Could not submit event: Synthetic event failure.",
    }),
  ).toBeVisible();
  await expect(sendButton).toBeEnabled();
  await expect(payloadInput).toHaveValue(/"status": "testing"/);
  expect(acceptedSubmission).toBeDefined();

  await page.unroute("**/relay-api/events");
  const retryResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname === "/relay-api/events"
  );
  await sendButton.click();
  const retryResponse = await retryResponsePromise;
  const retriedSubmission = await retryResponse.json() as {
    eventId: string;
    deliveryId: string;
    correlationId: string;
  };
  expect(retryResponse.status()).toBe(202);
  expect(retryResponse.headers()["idempotency-replayed"]).toBe("true");
  expect(retriedSubmission.eventId).toBe(acceptedSubmission?.eventId);
  expect(retriedSubmission.deliveryId).toBe(acceptedSubmission?.deliveryId);
  expect(retriedSubmission.correlationId).toBe(
    acceptedSubmission?.correlationId,
  );
  expect(idempotencyKeys).toHaveLength(2);
  expect(new Set(idempotencyKeys).size).toBe(1);
  await expect(
    page.getByText(/Event accepted\. Delivery .* is being tracked\./),
  ).toBeVisible();
  await expect(page.getByText(/Delivery .* succeeded\./)).toBeVisible();

  const historyResponse = await page.request.get(
    `/relay-api/deliveries?eventType=${encodeURIComponent(eventType)}&limit=20`,
  );
  const history = await historyResponse.json() as {
    items: Array<{ id: string }>;
  };
  expect(historyResponse.status()).toBe(200);
  expect(history.items).toHaveLength(1);
  expect(history.items[0]?.id).toBe(retriedSubmission.deliveryId);
});
