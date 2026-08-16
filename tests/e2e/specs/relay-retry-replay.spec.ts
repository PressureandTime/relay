import { expect, type Page, test } from "@playwright/test";

async function registerReceiver(
  page: Page,
  behavior: "retryThenSucceed" | "failUntilReplay",
  endpointName: string,
): Promise<string> {
  await page.goto("/");
  await page.getByRole("combobox", { name: "Receiver behavior" }).selectOption(behavior);
  await page.getByRole("button", { name: "Prepare receiver" }).click();
  await expect(
    page.getByText("Receiver prepared. Continue to endpoint registration."),
  ).toBeVisible();

  await page.getByRole("textbox", { name: "Endpoint name" }).fill(endpointName);
  const registrationResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname === "/relay-api/endpoints"
  );
  await page.getByRole("button", { name: "Register endpoint" }).click();
  const registrationResponse = await registrationResponsePromise;
  expect(registrationResponse.status()).toBe(201);
  await expect(
    page.getByText(new RegExp(`Endpoint .${endpointName}. registered`)),
  ).toBeVisible();
  const endpoint = await registrationResponse.json() as { id: string };
  return endpoint.id;
}

async function submitEvent(page: Page, eventType: string) {
  await page
    .getByRole("textbox", { name: "Event type", exact: true })
    .fill(eventType);
  await page.getByRole("textbox", { name: "JSON payload" }).fill(
    JSON.stringify(
      {
        fileId: "file_e2e",
        status: "testing",
      },
      null,
      2,
    ),
  );
  await page.getByRole("button", { name: "Send event" }).click();
  await expect(
    page.getByText(/Event accepted\. Delivery .* is being tracked\./),
  ).toBeVisible();
}

function attemptCard(page: Page, number: number) {
  return page.locator(".attemptCard").filter({
    has: page.getByRole("heading", { name: `Attempt ${number}` }),
  });
}

test("retries a delivery twice before succeeding", async ({ page }) => {
  const suffix = crypto.randomUUID().slice(0, 8);
  await registerReceiver(page, "retryThenSucceed", `Retry receiver ${suffix}`);
  await submitEvent(page, `relay.retry.${suffix}`);

  await expect(page.getByText(/Delivery .* succeeded\./)).toBeVisible();
  await expect(attemptCard(page, 1).getByText("503", { exact: true })).toBeVisible();
  await expect(attemptCard(page, 2).getByText("503", { exact: true })).toBeVisible();
  await expect(attemptCard(page, 3).getByText("204", { exact: true })).toBeVisible();
});

test("replays a failed delivery and shows its lineage", async ({ page }) => {
  const suffix = crypto.randomUUID().slice(0, 8);
  const endpointName = `Replay receiver ${suffix}`;
  const endpointId = await registerReceiver(page, "failUntilReplay", endpointName);
  await submitEvent(page, `relay.replay.${suffix}`);

  await expect(page.getByText(/Delivery .* failed\./)).toBeVisible();
  await expect(attemptCard(page, 4).getByText("503", { exact: true })).toBeVisible();

  const endpointRecord = page.getByRole("listitem").filter({ hasText: endpointId });
  await endpointRecord.getByRole("button", {
    name: `Disable endpoint ${endpointName}`,
  }).click();
  await expect(page.getByRole("button", { name: "Replay delivery" })).toBeDisabled();
  await expect(
    page.getByText("Reactivate the endpoint before scheduling a replay."),
  ).toBeVisible();
  await endpointRecord.getByRole("button", {
    name: `Reactivate endpoint ${endpointName}`,
  }).click();
  await expect(page.getByRole("button", { name: "Replay delivery" })).toBeEnabled();

  const replayResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST"
      && /\/relay-api\/deliveries\/[^/]+\/replays$/.test(response.url()),
  );
  await page.getByRole("button", { name: "Replay delivery" }).click();
  const replayResponse = await replayResponsePromise;
  expect(replayResponse.status()).toBe(202);

  await expect(page.getByText(/Replay scheduled\. New delivery .* is being tracked\./)).toBeVisible();
  await expect(page.getByText(/Delivery .* succeeded\./)).toBeVisible();
  await expect(page.getByText("Replay of", { exact: true })).toBeVisible();
  await expect(attemptCard(page, 1).getByText("204", { exact: true })).toBeVisible();
});
