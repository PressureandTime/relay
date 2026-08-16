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

  const paginationEventType = `relay.pagination.${suffix}`;
  for (let index = 0; index < 21; index++) {
    const response = await page.request.post("/relay-api/events", {
      headers: { "Idempotency-Key": crypto.randomUUID() },
      data: {
        endpointId: registeredEndpoint.id,
        type: paginationEventType,
        payload: {
          fileId: `file_page_${index}`,
          status: "queued",
        },
      },
    });
    expect(response.status()).toBe(202);
  }

  await page.reload();
  await page
    .getByRole("combobox", { name: "Delivery endpoint" })
    .selectOption(registeredEndpoint.id);
  await page
    .getByRole("textbox", { name: "Delivery event type" })
    .fill(paginationEventType);
  const paginationFilterResponsePromise = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "GET"
      && url.pathname === "/relay-api/deliveries"
      && url.searchParams.get("endpointId") === registeredEndpoint.id
      && url.searchParams.get("eventType") === paginationEventType;
  });
  await page.getByRole("button", { name: "Apply filters" }).click();
  expect((await paginationFilterResponsePromise).status()).toBe(200);

  const deliveryButtons = page.locator(".deliveryButton");
  await expect(deliveryButtons).toHaveCount(20);
  await expect(persistedDelivery).not.toBeVisible();

  let failNextContinuation = true;
  await page.route(/\/relay-api\/deliveries\?.*cursor=/, async (route) => {
    if (failNextContinuation) {
      failNextContinuation = false;
      await route.fulfill({
        status: 500,
        contentType: "application/problem+json",
        body: JSON.stringify({ title: "Synthetic continuation failure." }),
      });
      return;
    }
    await route.continue();
  });

  await page.getByRole("button", { name: "Load more" }).click();
  await expect(
    page.getByText(
      "Could not load older deliveries: Synthetic continuation failure.",
    ),
  ).toBeVisible();
  await expect(deliveryButtons).toHaveCount(20);
  expect(consoleErrors).toContain(
    "Failed to load resource: the server responded with a status of 500 (Internal Server Error)",
  );
  consoleErrors.length = 0;

  const continuationResponsePromise = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === "GET"
      && url.pathname === "/relay-api/deliveries"
      && Boolean(url.searchParams.get("cursor"))
      && url.searchParams.get("endpointId") === registeredEndpoint.id
      && url.searchParams.get("eventType") === paginationEventType;
  });
  await page.getByRole("button", { name: "Load more" }).click();
  expect((await continuationResponsePromise).status()).toBe(200);
  await expect(deliveryButtons).toHaveCount(21);
  await expect(persistedDelivery).not.toBeVisible();
  await expect(page.getByText("End of history.")).toBeVisible();
  await page.unroute(/\/relay-api\/deliveries\?.*cursor=/);
  expect(consoleErrors).toEqual([]);
});
