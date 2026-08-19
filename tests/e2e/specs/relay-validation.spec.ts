import { expect, test } from "@playwright/test";

test("shows event validation, loading, server error, and recovery states", async ({
  page,
}) => {
  const suffix = crypto.randomUUID().slice(0, 8);
  const endpointName = `Validation receiver ${suffix}`;

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
  let eventRequests = 0;
  page.on("request", (request) => {
    if (
      request.method() === "POST"
      && new URL(request.url()).pathname === "/relay-api/events"
    ) {
      eventRequests += 1;
    }
  });

  await payloadInput.fill("{not-json}");
  await sendButton.click();
  await expect(
    page.getByRole("alert").filter({ hasText: "Payload must be valid JSON." }),
  ).toBeVisible();
  expect(eventRequests).toBe(0);

  await payloadInput.fill(JSON.stringify({ status: "testing" }, null, 2));
  let releaseFailureResponse: () => void = () => undefined;
  const failureResponseGate = new Promise<void>((resolve) => {
    releaseFailureResponse = resolve;
  });
  await page.route("**/relay-api/events", async (route) => {
    await failureResponseGate;
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

  await page.unroute("**/relay-api/events");
  await sendButton.click();
  await expect(
    page.getByText(/Event accepted\. Delivery .* is being tracked\./),
  ).toBeVisible();
  await expect(page.getByText(/Delivery .* succeeded\./)).toBeVisible();
  expect(eventRequests).toBe(2);
});
