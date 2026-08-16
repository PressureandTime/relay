import { expect, test } from "@playwright/test";

test("registers an endpoint, delivers an event, and restores its status", async ({
  page,
}) => {
  const suffix = crypto.randomUUID().slice(0, 8);
  const endpointName = `Test endpoint ${suffix}`;
  const eventType = `relay.e2e.${suffix}`;
  const consoleErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });

  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Relay" }),
  ).toBeVisible();

  const prepareButton = page.getByRole("button", {
    name: "Prepare receiver",
  });
  await prepareButton.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByText("Receiver prepared. Continue to endpoint registration."),
  ).toBeVisible();

  const endpointNameInput = page.getByRole("textbox", {
    name: "Endpoint name",
  });
  const signingSecretInput = page.getByRole("textbox", {
    name: "Signing secret",
  });
  await endpointNameInput.fill(endpointName);
  await expect(signingSecretInput).not.toHaveValue("");

  const registerButton = page.getByRole("button", {
    name: "Register endpoint",
  });
  const registrationResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname === "/relay-api/endpoints"
  );
  await registerButton.focus();
  await page.keyboard.press("Enter");
  const registrationResponse = await registrationResponsePromise;
  expect(registrationResponse.status()).toBe(201);
  const registeredEndpoint = await registrationResponse.json() as { id: string };
  await expect(
    page.getByText(new RegExp(`Endpoint .${endpointName}. registered`)),
  ).toBeVisible();
  await expect(signingSecretInput).toHaveValue("");
  await expect(page.getByRole("combobox", { name: "Endpoint", exact: true })).toHaveValue(
    /.+/,
  );

  const endpointRecord = page
    .getByRole("listitem")
    .filter({ hasText: registeredEndpoint.id });
  await expect(endpointRecord.getByText("Active", { exact: true })).toBeVisible();
  const disableResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname
        === `/relay-api/endpoints/${registeredEndpoint.id}/disable`
  );
  await endpointRecord.getByRole("button", {
    name: `Disable endpoint ${endpointName}`,
  }).click();
  expect((await disableResponsePromise).status()).toBe(200);
  await expect(endpointRecord.getByText("Disabled", { exact: true })).toBeVisible();
  await expect(
    page
      .getByRole("combobox", { name: "Endpoint", exact: true })
      .locator(`option[value="${registeredEndpoint.id}"]`),
  ).toHaveCount(0);

  const reactivateResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname
        === `/relay-api/endpoints/${registeredEndpoint.id}/reactivate`
  );
  await endpointRecord.getByRole("button", {
    name: `Reactivate endpoint ${endpointName}`,
  }).click();
  expect((await reactivateResponsePromise).status()).toBe(200);
  await expect(endpointRecord.getByText("Active", { exact: true })).toBeVisible();
  await page
    .getByRole("combobox", { name: "Endpoint", exact: true })
    .selectOption(registeredEndpoint.id);

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

  const submitButton = page.getByRole("button", {
    name: "Send event",
  });
  await submitButton.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByText(/Event accepted\. Delivery .* is being tracked\./),
  ).toBeVisible();
  await expect(page.getByText(/Delivery .* succeeded\./)).toBeVisible();
  await expect(page.getByRole("heading", { name: "Attempt 1" })).toBeVisible();
  await expect(page.getByText("204", { exact: true })).toBeVisible();
  await expect(page.getByText(/\d+ ms/)).toBeVisible();

  await page.reload();
  const persistedDelivery = page
    .getByRole("button")
    .filter({ hasText: eventType })
    .filter({ hasText: "Succeeded" });
  await expect(persistedDelivery).toBeVisible();
  await persistedDelivery.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Attempt 1" })).toBeVisible();
  await expect(page.getByText("204", { exact: true })).toBeVisible();

  await page
    .getByRole("combobox", { name: "Delivery state" })
    .selectOption("Succeeded");
  await page
    .getByRole("combobox", { name: "Delivery endpoint" })
    .selectOption({ label: endpointName });
  await page
    .getByRole("textbox", { name: "Delivery event type" })
    .fill(eventType);
  const filteredResponsePromise = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "GET"
      && url.pathname === "/relay-api/deliveries"
      && url.searchParams.get("state") === "Succeeded"
      && url.searchParams.get("eventType") === eventType
      && Boolean(url.searchParams.get("endpointId"));
  });
  await page.getByRole("button", { name: "Apply filters" }).click();
  expect((await filteredResponsePromise).status()).toBe(200);
  await expect(page.getByText("1 matching delivery loaded.")).toBeVisible();
  await expect(persistedDelivery).toBeVisible();

  await page
    .getByRole("textbox", { name: "Delivery event type" })
    .fill(`${eventType}.missing`);
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(
    page.getByText("No deliveries match the applied filters."),
  ).toBeVisible();
  await expect(persistedDelivery).not.toBeVisible();

  await page.getByRole("button", { name: "Reset" }).click();
  await expect(page.getByText(/recent deliveries loaded\./)).toBeVisible();
  await expect(persistedDelivery).toBeVisible();
  expect(consoleErrors).toEqual([]);
});
