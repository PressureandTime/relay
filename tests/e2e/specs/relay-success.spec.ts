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
    name: "Prepare success receiver",
  });
  await prepareButton.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByText("Success receiver prepared. Continue to endpoint registration."),
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
  await registerButton.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByText(new RegExp(`Endpoint .${endpointName}. registered`)),
  ).toBeVisible();
  await expect(signingSecretInput).toHaveValue("");
  await expect(page.getByRole("combobox", { name: "Endpoint" })).toHaveValue(
    /.+/,
  );

  await page.getByRole("textbox", { name: "Event type" }).fill(eventType);
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
  expect(consoleErrors).toEqual([]);
});
