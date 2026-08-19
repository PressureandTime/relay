"use client";

import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  type DeliveryDetail,
  type DeliverySummary,
  type Endpoint,
  type EventAcceptedResponse,
  type Receiver,
  type ReceiverResponse,
  type ReplayAcceptedResponse,
  apiErrorMessage,
  isEndpointActive,
  normalizeDeliveryDetail,
  normalizeDeliveryHistoryPage,
  normalizeEndpoint,
} from "@/lib/contracts";
import {
  type ReplayIntent,
  resolveReplayIntent,
} from "@/lib/replay-intent";
import {
  type EventSubmissionIntent,
  resolveEventSubmissionIntent,
} from "@/lib/event-submission-intent";
import {
  DELIVERY_STATES,
  EMPTY_DELIVERY_FILTERS,
  type DeliveryFilters,
  appendUniqueDeliveries,
  deliveryHistoryPath,
  deliveryMatchesFilters,
  hasDeliveryFilters,
  normalizeDeliveryFilters,
} from "@/lib/delivery-filters";

const POLL_INTERVAL_MS = 1_000;
const MAX_POLL_ATTEMPTS = 30;
const TERMINAL_STATES = new Set(["succeeded", "failed"]);
const DEFAULT_PAYLOAD = JSON.stringify(
  {
    "fileId": "file_001",
    "status": "processed"
  },
  null,
  2,
);

type RequestPhase = "idle" | "loading" | "success" | "error";

interface RequestStatus {
  phase: RequestPhase;
  message: string;
}

interface RelayDashboardProps {
  initialEndpoints: Endpoint[];
  initialEndpointError?: string;
  initialDeliveries: DeliverySummary[];
  initialNextDeliveryCursor?: string;
  initialDeliveryError?: string;
}

interface HistoryLoadOptions {
  append?: boolean;
  cursor?: string;
}

const idleReceiver: RequestStatus = {
  phase: "idle",
  message: "No receiver prepared yet.",
};

const idleEndpoint: RequestStatus = {
  phase: "idle",
  message: "Prepare a receiver before registering its endpoint.",
};

const idleEvent: RequestStatus = {
  phase: "idle",
  message: "Register or select an endpoint to submit an event.",
};

function formatUtc(value?: string): string {
  if (!value) {
    return "Time unavailable";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat("en", {
    dateStyle: "medium",
    timeStyle: "medium",
    timeZone: "UTC",
  }).format(date);
}

function statusTone(state: string): string {
  const normalized = state.toLowerCase();
  if (normalized === "succeeded" || normalized === "active") {
    return "success";
  }
  if (normalized === "failed") {
    return "error";
  }
  if (
    ["accepted", "pending", "queued", "processing", "delivering", "retryscheduled"].includes(
      normalized,
    )
  ) {
    return "active";
  }
  return "neutral";
}

function isTerminal(state: string): boolean {
  return TERMINAL_STATES.has(state.toLowerCase());
}

function historyLoadedMessage(count: number, filtered: boolean): string {
  return `${count} ${filtered ? "matching " : "recent "}${count === 1 ? "delivery" : "deliveries"} loaded.`;
}

async function requestJson<T>(
  input: string,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(input, {
    ...init,
    headers: {
      Accept: "application/json",
      ...init?.headers,
    },
  });
  const body: unknown = await response.json().catch(() => undefined);

  if (!response.ok) {
    throw new Error(
      apiErrorMessage(body) ?? `Request failed (${response.status}).`,
    );
  }

  return body as T;
}

function StatusMessage({
  status,
  announce = true,
}: {
  status: RequestStatus;
  announce?: boolean;
}) {
  const role = announce
    ? status.phase === "error"
      ? "alert"
      : "status"
    : undefined;
  return (
    <p className={`requestStatus requestStatus--${status.phase}`} role={role}>
      {status.message}
    </p>
  );
}

function StateBadge({ state }: { state: string }) {
  return (
    <span className={`stateBadge stateBadge--${statusTone(state)}`}>
      {state}
    </span>
  );
}

export function RelayDashboard({
  initialEndpoints,
  initialEndpointError,
  initialDeliveries,
  initialNextDeliveryCursor,
  initialDeliveryError,
}: RelayDashboardProps) {
  const [receiver, setReceiver] = useState<Receiver | null>(null);
  const [signingSecret, setSigningSecret] = useState("");
  const [receiverBehavior, setReceiverBehavior] = useState("success");
  const [receiverStatus, setReceiverStatus] =
    useState<RequestStatus>(idleReceiver);
  const [endpointName, setEndpointName] = useState("Success receiver");
  const [endpointStatus, setEndpointStatus] =
    useState<RequestStatus>(idleEndpoint);
  const [endpoints, setEndpoints] = useState(initialEndpoints);
  const [endpointListError, setEndpointListError] = useState(
    initialEndpointError ?? "",
  );
  const [endpointActionStatus, setEndpointActionStatus] =
    useState<RequestStatus>({ phase: "idle", message: "" });
  const [changingEndpointId, setChangingEndpointId] = useState("");
  const [selectedEndpointId, setSelectedEndpointId] = useState(
    initialEndpoints.find(isEndpointActive)?.id ?? "",
  );
  const [eventType, setEventType] = useState("file.processed");
  const [eventPayload, setEventPayload] = useState(DEFAULT_PAYLOAD);
  const [eventStatus, setEventStatus] = useState<RequestStatus>(idleEvent);
  const [deliveries, setDeliveries] = useState(initialDeliveries);
  const [nextDeliveryCursor, setNextDeliveryCursor] = useState(
    initialNextDeliveryCursor ?? "",
  );
  const [deliveryFilterDraft, setDeliveryFilterDraft] =
    useState<DeliveryFilters>(() => ({ ...EMPTY_DELIVERY_FILTERS }));
  const [appliedDeliveryFilters, setAppliedDeliveryFilters] =
    useState<DeliveryFilters>(() => ({ ...EMPTY_DELIVERY_FILTERS }));
  const [historyStatus, setHistoryStatus] = useState<RequestStatus>(() => {
    if (initialDeliveryError) {
      return { phase: "error", message: initialDeliveryError };
    }
    if (initialDeliveries.length === 0) {
      return { phase: "idle", message: "No deliveries have been recorded." };
    }
    return {
      phase: "success",
      message: historyLoadedMessage(initialDeliveries.length, false),
    };
  });
  const [selectedDeliveryId, setSelectedDeliveryId] = useState("");
  const [deliveryDetail, setDeliveryDetail] =
    useState<DeliveryDetail | null>(null);
  const [detailStatus, setDetailStatus] = useState<RequestStatus>({
    phase: "idle",
    message: "Select a delivery to inspect its attempts.",
  });
  const [trackedDeliveryId, setTrackedDeliveryId] = useState("");
  const [pollStatus, setPollStatus] = useState<RequestStatus>({
    phase: "idle",
    message: "No delivery is being tracked.",
  });
  const [replayStatus, setReplayStatus] = useState<RequestStatus>({
    phase: "idle",
    message: "",
  });
  const [announcement, setAnnouncement] = useState(
    "Dashboard ready. Follow the three steps to send a synthetic event.",
  );
  const detailControllerRef = useRef<AbortController | null>(null);
  const eventSubmissionIntentRef = useRef<EventSubmissionIntent | null>(null);
  const replayIntentRef = useRef<ReplayIntent | null>(null);
  const historyRequestIdRef = useRef(0);

  const loadHistory = useCallback(async (
    filters: DeliveryFilters,
    options: HistoryLoadOptions = {},
  ) => {
    const requestId = ++historyRequestIdRef.current;
    const append = options.append === true;
    setHistoryStatus({
      phase: "loading",
      message: append ? "Loading older deliveries…" : "Refreshing deliveries…",
    });
    if (!append) {
      setNextDeliveryCursor("");
    }

    try {
      const body = await requestJson<unknown>(deliveryHistoryPath(filters, {
        cursor: options.cursor,
      }));
      const page = normalizeDeliveryHistoryPage(body);
      if (!page) {
        throw new Error("Delivery history response was not in the expected format.");
      }
      if (requestId !== historyRequestIdRef.current) return;

      setDeliveries((current) =>
        append ? appendUniqueDeliveries(current, page.items) : page.items,
      );
      setNextDeliveryCursor(page.nextCursor ?? "");
      setHistoryStatus(
        append
          ? {
              phase: "success",
              message: page.items.length === 0
                ? "End of delivery history reached."
                : `${page.items.length} older ${page.items.length === 1 ? "delivery" : "deliveries"} loaded.`,
            }
          : page.items.length === 0
          ? {
              phase: "idle",
              message: hasDeliveryFilters(filters)
                ? "No deliveries match the applied filters."
                : "No deliveries have been recorded.",
            }
          : {
              phase: "success",
              message: historyLoadedMessage(
                page.items.length,
                hasDeliveryFilters(filters),
              ),
            },
      );
    } catch (error) {
      if (requestId !== historyRequestIdRef.current) return;
      setHistoryStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not ${append ? "load older" : "refresh"} deliveries: ${error.message}`
            : `Could not ${append ? "load older" : "refresh"} deliveries.`,
      });
    }
  }, []);

  const refreshHistory = useCallback(
    async () => {
      await loadHistory(appliedDeliveryFilters);
    },
    [appliedDeliveryFilters, loadHistory],
  );

  useEffect(() => {
    return () => detailControllerRef.current?.abort();
  }, []);

  useEffect(() => {
    if (!trackedDeliveryId) {
      return;
    }

    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let pollCount = 0;

    async function pollDelivery() {
      pollCount += 1;

      try {
        const body = await requestJson<unknown>(
          `/relay-api/deliveries/${encodeURIComponent(trackedDeliveryId)}`,
          { signal: controller.signal },
        );
        const detail = normalizeDeliveryDetail(body);
        if (!detail) {
          throw new Error("Delivery detail was not in the expected format.");
        }

        setSelectedDeliveryId(detail.id);
        setDeliveryDetail(detail);
        setDetailStatus({
          phase: "success",
          message: `Delivery detail loaded with ${detail.attempts.length} attempt${detail.attempts.length === 1 ? "" : "s"}.`,
        });
        setDeliveries((current) => {
          const remainder = current.filter((delivery) => delivery.id !== detail.id);
          return deliveryMatchesFilters(detail, appliedDeliveryFilters)
            ? [detail, ...remainder]
            : remainder;
        });

        if (isTerminal(detail.state)) {
          const succeeded = detail.state.toLowerCase() === "succeeded";
          setPollStatus({
            phase: succeeded ? "success" : "error",
            message: `Delivery ${detail.id} ${detail.state.toLowerCase()}.`,
          });
          setAnnouncement(
            `Delivery ${detail.id} finished with state ${detail.state}.`,
          );
          await refreshHistory();
          setTrackedDeliveryId((current) =>
            current === detail.id ? "" : current,
          );
          return;
        }

        setPollStatus({
          phase: "loading",
          message: `Delivery state: ${detail.state}. Check ${pollCount} of ${MAX_POLL_ATTEMPTS}.`,
        });
      } catch (error) {
        if (error instanceof DOMException && error.name === "AbortError") {
          return;
        }

        if (pollCount >= MAX_POLL_ATTEMPTS) {
          setPollStatus({
            phase: "error",
            message: "Delivery tracking stopped after 30 checks. Refresh the history to get the latest state.",
          });
          setTrackedDeliveryId((current) =>
            current === trackedDeliveryId ? "" : current,
          );
          return;
        }

        setPollStatus({
          phase: "loading",
          message: `Could not check delivery yet. Retrying (${pollCount} of ${MAX_POLL_ATTEMPTS})…`,
        });
      }

      if (pollCount >= MAX_POLL_ATTEMPTS) {
        setPollStatus({
          phase: "error",
          message: "Delivery tracking stopped after 30 checks. Refresh the history to get the latest state.",
        });
        setTrackedDeliveryId((current) =>
          current === trackedDeliveryId ? "" : current,
        );
        return;
      }

      timer = setTimeout(pollDelivery, POLL_INTERVAL_MS);
    }

    void pollDelivery();

    return () => {
      controller.abort();
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [appliedDeliveryFilters, refreshHistory, trackedDeliveryId]);

  async function applyDeliveryFilters(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const filters = normalizeDeliveryFilters(deliveryFilterDraft);
    setDeliveryFilterDraft(filters);
    setAppliedDeliveryFilters(filters);
    await loadHistory(filters);
  }

  async function resetDeliveryFilters() {
    const filters = { ...EMPTY_DELIVERY_FILTERS };
    setDeliveryFilterDraft(filters);
    setAppliedDeliveryFilters(filters);
    await loadHistory(filters);
  }

  async function loadMoreDeliveries() {
    if (!nextDeliveryCursor) return;
    await loadHistory(appliedDeliveryFilters, {
      append: true,
      cursor: nextDeliveryCursor,
    });
  }

  async function prepareReceiver(event?: FormEvent<HTMLFormElement>) {
    if (event) event.preventDefault();
    setReceiver(null);
    setSigningSecret("");
    setReceiverStatus({
      phase: "loading",
      message: "Preparing an ephemeral receiver…",
    });
    setEndpointStatus(idleEndpoint);

    try {
      const body = await requestJson<ReceiverResponse>(
        "/receiver-control/receivers",
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ behavior: receiverBehavior }),
        },
      );
      if (!body.id || !body.url || !body.signingSecret) {
        throw new Error("Receiver response was missing required fields.");
      }

      setReceiver({ id: body.id, url: body.url });
      setSigningSecret(body.signingSecret);
      setReceiverStatus({
        phase: "success",
        message: "Receiver prepared. Continue to endpoint registration.",
      });
      setAnnouncement("Receiver prepared.");
    } catch (error) {
      setReceiverStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not prepare receiver: ${error.message}`
            : "Could not prepare receiver.",
      });
    }
  }

  async function registerEndpoint(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!receiver || !signingSecret) {
      setEndpointStatus({
        phase: "error",
        message: "Prepare a receiver before registering an endpoint.",
      });
      return;
    }

    const name = endpointName.trim();
    if (!name) {
      setEndpointStatus({
        phase: "error",
        message: "Enter an endpoint name.",
      });
      return;
    }

    setEndpointStatus({
      phase: "loading",
      message: "Registering endpoint…",
    });

    try {
      const body = await requestJson<unknown>("/relay-api/endpoints", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name,
          url: receiver.url,
          signingSecret,
        }),
      });
      const endpoint = normalizeEndpoint(body);
      if (!endpoint) {
        throw new Error("Endpoint response was not in the expected format.");
      }

      setSigningSecret("");
      setEndpoints((current) => [
        endpoint,
        ...current.filter((item) => item.id !== endpoint.id),
      ]);
      setEndpointListError("");
      setSelectedEndpointId(endpoint.id);
      setEndpointStatus({
        phase: "success",
        message: `Endpoint “${endpoint.name}” registered. The signing secret was cleared from the form.`,
      });
      setAnnouncement("Endpoint registered.");
    } catch (error) {
      setEndpointStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not register endpoint: ${error.message}`
            : "Could not register endpoint.",
      });
    }
  }

  async function changeEndpointState(
    endpoint: Endpoint,
    action: "disable" | "reactivate",
  ) {
    setChangingEndpointId(endpoint.id);
    setEndpointActionStatus({
      phase: "loading",
      message: `${action === "disable" ? "Disabling" : "Reactivating"} endpoint…`,
    });

    try {
      const body = await requestJson<unknown>(
        `/relay-api/endpoints/${encodeURIComponent(endpoint.id)}/${action}`,
        { method: "POST" },
      );
      const updatedEndpoint = normalizeEndpoint(body);
      if (!updatedEndpoint) {
        throw new Error("Endpoint response was not in the expected format.");
      }

      setEndpoints((current) =>
        current.map((item) =>
          item.id === updatedEndpoint.id ? updatedEndpoint : item,
        ),
      );
      if (action === "disable") {
        const fallbackId = endpoints.find(
          (item) => item.id !== updatedEndpoint.id && isEndpointActive(item),
        )?.id ?? "";
        setSelectedEndpointId((current) =>
          current === updatedEndpoint.id ? fallbackId : current,
        );
        if (!fallbackId) {
          setEventStatus({
            phase: "idle",
            message: "Reactivate an endpoint before submitting an event.",
          });
        }
      } else {
        setSelectedEndpointId((current) => current || updatedEndpoint.id);
      }
      setEndpointActionStatus({
        phase: "success",
        message: `Endpoint “${updatedEndpoint.name}” is now ${updatedEndpoint.state.toLowerCase()}.`,
      });
      setAnnouncement(`Endpoint ${updatedEndpoint.state.toLowerCase()}.`);
    } catch (error) {
      setEndpointActionStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not ${action} endpoint: ${error.message}`
            : `Could not ${action} endpoint.`,
      });
    } finally {
      setChangingEndpointId("");
    }
  }

  async function submitEvent(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selectedEndpointId) {
      setEventStatus({
        phase: "error",
        message: "Select an endpoint before submitting an event.",
      });
      return;
    }

    const type = eventType.trim();
    if (!type) {
      setEventStatus({ phase: "error", message: "Enter an event type." });
      return;
    }

    let payload: unknown;
    try {
      payload = JSON.parse(eventPayload);
    } catch {
      setEventStatus({
        phase: "error",
        message: "Payload must be valid JSON.",
      });
      return;
    }

    setTrackedDeliveryId("");
    setReplayStatus({ phase: "idle", message: "" });
    setEventStatus({ phase: "loading", message: "Submitting event…" });
    setPollStatus({
      phase: "idle",
      message: "Waiting for the event to be accepted.",
    });
    const requestBody = JSON.stringify({
      endpointId: selectedEndpointId,
      type,
      payload,
    });
    const submissionIntent = resolveEventSubmissionIntent(
      requestBody,
      eventSubmissionIntentRef.current,
    );
    eventSubmissionIntentRef.current = submissionIntent;

    try {
      const body = await requestJson<EventAcceptedResponse>(
        "/relay-api/events",
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Idempotency-Key": submissionIntent.idempotencyKey,
          },
          body: requestBody,
        },
      );
      if (!body.deliveryId || !body.eventId || !body.state) {
        throw new Error("Event response was missing required fields.");
      }
      if (
        eventSubmissionIntentRef.current?.idempotencyKey ===
        submissionIntent.idempotencyKey
      ) {
        eventSubmissionIntentRef.current = null;
      }

      const acceptedDelivery: DeliverySummary = {
        id: body.deliveryId,
        eventId: body.eventId,
        endpointId: selectedEndpointId,
        endpointName: selectedEndpoint?.name,
        eventType: type,
        state: body.state,
        correlationId: body.correlationId,
      };
      setDeliveries((current) =>
        deliveryMatchesFilters(acceptedDelivery, appliedDeliveryFilters)
          ? [
              acceptedDelivery,
              ...current.filter((delivery) => delivery.id !== body.deliveryId),
            ]
          : current,
      );
      setSelectedDeliveryId(body.deliveryId);
      setDeliveryDetail(null);
      setDetailStatus({
        phase: "loading",
        message: "Waiting for the first delivery attempt…",
      });
      setEventStatus({
        phase: "success",
        message: `Event accepted. Delivery ${body.deliveryId} is being tracked.`,
      });
      setPollStatus({
        phase: "loading",
        message: "Checking delivery state…",
      });
      setAnnouncement("Event accepted, tracking delivery.");
      setTrackedDeliveryId(body.deliveryId);
    } catch (error) {
      setEventStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not submit event: ${error.message}`
            : "Could not submit event.",
      });
    }
  }

  async function selectDelivery(deliveryId: string) {
    detailControllerRef.current?.abort();
    const controller = new AbortController();
    detailControllerRef.current = controller;
    setReplayStatus({ phase: "idle", message: "" });
    setSelectedDeliveryId(deliveryId);
    setDeliveryDetail(null);
    setDetailStatus({
      phase: "loading",
      message: `Loading delivery ${deliveryId}…`,
    });

    try {
      const body = await requestJson<unknown>(
        `/relay-api/deliveries/${encodeURIComponent(deliveryId)}`,
        { signal: controller.signal },
      );
      const detail = normalizeDeliveryDetail(body);
      if (!detail) {
        throw new Error("Delivery detail was not in the expected format.");
      }
      setDeliveryDetail(detail);
      setDetailStatus({
        phase: "success",
        message: `Delivery detail loaded with ${detail.attempts.length} attempt${detail.attempts.length === 1 ? "" : "s"}.`,
      });
      setAnnouncement(`Delivery ${detail.id} detail loaded.`);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }
      setDetailStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not load delivery: ${error.message}`
            : "Could not load delivery.",
      });
    } finally {
      if (detailControllerRef.current === controller) {
        detailControllerRef.current = null;
      }
    }
  }

  async function replayDelivery() {
    if (!deliveryDetail || deliveryDetail.state.toLowerCase() !== "failed") {
      return;
    }

    setReplayStatus({
      phase: "loading",
      message: "Replaying delivery…",
    });
    const replayIntent = resolveReplayIntent(
      deliveryDetail.id,
      replayIntentRef.current,
    );
    replayIntentRef.current = replayIntent;

    try {
      const body = await requestJson<ReplayAcceptedResponse>(
        `/relay-api/deliveries/${encodeURIComponent(deliveryDetail.id)}/replays`,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Idempotency-Key": replayIntent.idempotencyKey,
          },
        },
      );
      if (!body.deliveryId || !body.originalDeliveryId || !body.state) {
        throw new Error("Replay response was missing required fields.");
      }
      if (
        replayIntentRef.current?.idempotencyKey ===
        replayIntent.idempotencyKey
      ) {
        replayIntentRef.current = null;
      }

      setReplayStatus({
        phase: "success",
        message: `Replay scheduled. New delivery ${body.deliveryId} is being tracked.`,
      });
      setSelectedDeliveryId(body.deliveryId);
      setDeliveryDetail(null);
      setDetailStatus({
        phase: "loading",
        message: "Waiting for the replay delivery attempt…",
      });
      setPollStatus({
        phase: "loading",
        message: "Checking replay delivery state…",
      });
      setAnnouncement("Delivery replay scheduled.");
      setTrackedDeliveryId(body.deliveryId);
    } catch (error) {
      setReplayStatus({
        phase: "error",
        message:
          error instanceof Error
            ? `Could not replay: ${error.message}`
            : "Could not replay delivery.",
      });
    }
  }

  const activeEndpoints = endpoints.filter(isEndpointActive);
  const selectedEndpoint = activeEndpoints.find(
    (endpoint) => endpoint.id === selectedEndpointId,
  );
  const replayEndpoint = endpoints.find(
    (endpoint) => endpoint.id === deliveryDetail?.endpointId,
  );
  const replayBlocked = replayEndpoint !== undefined
    && !isEndpointActive(replayEndpoint);

  return (
    <main className="shell">
      <div className="srOnly" aria-live="polite" aria-atomic="true">
        {announcement}
      </div>

      <header className="pageHeader">
        <h1>Relay</h1>
        <p className="lede">
          Prepare a local receiver, register its endpoint, and send an event to track delivery.
        </p>
      </header>

      <div className="dashboardGrid">
        <section className="workflow" aria-labelledby="workflow-title">
          <div className="sectionHeading">
            <div>
              <h2 id="workflow-title">Send event</h2>
            </div>
          </div>

          <ol className="steps">
            <li className="stepCard">
              <div className="stepHeading">

                <div>
                  <h3>Prepare receiver</h3>
                  <p>Create an ephemeral receiver for the target endpoint.</p>
                </div>
              </div>
              <form onSubmit={prepareReceiver} className="formStack">
                <div className="field">
                  <label htmlFor="receiver-behavior">Receiver behavior</label>
                  <select
                    id="receiver-behavior"
                    value={receiverBehavior}
                    onChange={(e) => setReceiverBehavior(e.target.value)}
                    disabled={receiverStatus.phase === "loading"}
                  >
                    <option value="success">Success (always 204)</option>
                    <option value="retryThenSucceed">Retry then succeed (503, 503, 204)</option>
                    <option value="failUntilReplay">Fail until replay (4x 503, then 204)</option>
                    <option value="alwaysFail">Always fail (always 500)</option>
                  </select>
                </div>
                <button
                  className="button button--primary"
                  type="submit"
                  disabled={receiverStatus.phase === "loading"}
                >
                  {receiverStatus.phase === "loading"
                    ? "Preparing…"
                    : receiver
                      ? "Prepare another receiver"
                      : "Prepare receiver"}
                </button>
              </form>
              {receiver ? (
                <dl className="compactDetails">
                  <div>
                    <dt>Receiver ID</dt>
                    <dd>{receiver.id}</dd>
                  </div>
                  <div>
                    <dt>Receiver URL</dt>
                    <dd>{receiver.url}</dd>
                  </div>
                </dl>
              ) : null}
              <StatusMessage status={receiverStatus} />
            </li>

            <li className="stepCard">
              <div className="stepHeading">

                <div>
                  <h3>Register endpoint</h3>
                  <p>The signing secret stays only in this form and is cleared on success.</p>
                </div>
              </div>
              <form onSubmit={registerEndpoint} className="formStack">
                <div className="field">
                  <label htmlFor="endpoint-name">Endpoint name</label>
                  <input
                    id="endpoint-name"
                    name="endpointName"
                    value={endpointName}
                    onChange={(event) => setEndpointName(event.target.value)}
                    autoComplete="off"
                    required
                    disabled={!receiver || endpointStatus.phase === "loading"}
                  />
                </div>
                <div className="field">
                  <label htmlFor="endpoint-url">Receiver URL</label>
                  <input
                    id="endpoint-url"
                    name="endpointUrl"
                    value={receiver?.url ?? ""}
                    readOnly
                    disabled={!receiver}
                    placeholder="Prepare a receiver first"
                  />
                </div>
                <div className="field">
                  <label htmlFor="signing-secret">Signing secret</label>
                  <input
                    id="signing-secret"
                    name="signingSecret"
                    type="password"
                    value={signingSecret}
                    onChange={(event) => setSigningSecret(event.target.value)}
                    autoComplete="new-password"
                    required
                    disabled={!receiver || endpointStatus.phase === "loading"}
                  />
                </div>
                <button
                  className="button button--primary"
                  type="submit"
                  disabled={!receiver || endpointStatus.phase === "loading"}
                >
                  {endpointStatus.phase === "loading"
                    ? "Registering…"
                    : "Register endpoint"}
                </button>
              </form>
              <StatusMessage status={endpointStatus} />
            </li>

            <li className="stepCard">
              <div className="stepHeading">

                <div>
                  <h3>Submit event</h3>
                  <p>Send generic JSON with a new idempotency key for this request.</p>
                </div>
              </div>
              <form onSubmit={submitEvent} className="formStack">
                <div className="field">
                  <label htmlFor="event-endpoint">Endpoint</label>
                  <select
                    id="event-endpoint"
                    name="endpointId"
                    value={selectedEndpointId}
                    onChange={(event) => setSelectedEndpointId(event.target.value)}
                    disabled={activeEndpoints.length === 0 || eventStatus.phase === "loading"}
                    required
                  >
                    {activeEndpoints.length === 0 ? (
                      <option value="">No active endpoints available</option>
                    ) : null}
                    {activeEndpoints.map((endpoint) => (
                      <option key={endpoint.id} value={endpoint.id}>
                        {endpoint.name}
                      </option>
                    ))}
                  </select>
                  {selectedEndpoint ? (
                    <span className="fieldHint">Target: {selectedEndpoint.url}</span>
                  ) : null}
                </div>
                <div className="field">
                  <label htmlFor="event-type">Event type</label>
                  <input
                    id="event-type"
                    name="eventType"
                    value={eventType}
                    onChange={(event) => setEventType(event.target.value)}
                    required
                    disabled={eventStatus.phase === "loading"}
                  />
                </div>
                <div className="field">
                  <label htmlFor="event-payload">JSON payload</label>
                  <textarea
                    id="event-payload"
                    name="payload"
                    value={eventPayload}
                    onChange={(event) => setEventPayload(event.target.value)}
                    rows={7}
                    spellCheck={false}
                    required
                    disabled={eventStatus.phase === "loading"}
                  />
                </div>
                <button
                  className="button button--primary"
                  type="submit"
                  disabled={activeEndpoints.length === 0 || eventStatus.phase === "loading"}
                >
                  {eventStatus.phase === "loading"
                    ? "Submitting…"
                    : "Send event"}
                </button>
              </form>
              <StatusMessage status={eventStatus} />
              {pollStatus.phase !== "idle" ? (
                <StatusMessage status={pollStatus} announce={false} />
              ) : null}
            </li>
          </ol>
        </section>

        <aside className="sideColumn" aria-label="Relay records">
          <section className="recordPanel" aria-labelledby="endpoints-title">
            <div className="sectionHeading sectionHeading--compact">
              <div>
                <h2 id="endpoints-title">Endpoints</h2>
              </div>
              <span className="countBadge">{endpoints.length}</span>
            </div>
            {endpointActionStatus.phase !== "idle" ? (
              <StatusMessage status={endpointActionStatus} />
            ) : null}
            {endpointListError ? (
              <p className="requestStatus requestStatus--error" role="alert">
                {endpointListError}
              </p>
            ) : endpoints.length === 0 ? (
              <p className="emptyState">No endpoints are registered.</p>
            ) : (
              <ul className="recordList">
                {endpoints.map((endpoint) => (
                  <li key={endpoint.id}>
                    <span className="endpointRecord__heading">
                      <strong>{endpoint.name}</strong>
                      <StateBadge state={endpoint.state} />
                    </span>
                    <span>{endpoint.url}</span>
                    <code>{endpoint.id}</code>
                    <span className="endpointRecord__actions">
                      <button
                        className="button button--secondary button--small"
                        type="button"
                        aria-label={`${isEndpointActive(endpoint) ? "Disable" : "Reactivate"} endpoint ${endpoint.name}`}
                        onClick={() => void changeEndpointState(
                          endpoint,
                          isEndpointActive(endpoint) ? "disable" : "reactivate",
                        )}
                        disabled={changingEndpointId !== ""}
                      >
                        {changingEndpointId === endpoint.id
                          ? "Updating…"
                          : isEndpointActive(endpoint)
                            ? "Disable"
                            : "Reactivate"}
                      </button>
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="recordPanel" aria-labelledby="history-title">
            <div className="sectionHeading sectionHeading--compact">
              <div>
                <h2 id="history-title">Deliveries</h2>
              </div>
              <button
                className="button button--secondary button--small"
                type="button"
                onClick={() => void refreshHistory()}
                disabled={historyStatus.phase === "loading"}
              >
                {historyStatus.phase === "loading" ? "Refreshing…" : "Refresh"}
              </button>
            </div>
            <form
              className="deliveryFilters"
              aria-label="Delivery filters"
              onSubmit={(event) => void applyDeliveryFilters(event)}
            >
              <div className="field field--compact">
                <label htmlFor="delivery-state-filter">Delivery state</label>
                <select
                  id="delivery-state-filter"
                  value={deliveryFilterDraft.state}
                  onChange={(event) =>
                    setDeliveryFilterDraft((current) => ({
                      ...current,
                      state: event.target.value,
                    }))
                  }
                  disabled={historyStatus.phase === "loading"}
                >
                  <option value="">All states</option>
                  {DELIVERY_STATES.map((state) => (
                    <option key={state} value={state}>
                      {state}
                    </option>
                  ))}
                </select>
              </div>
              <div className="field field--compact">
                <label htmlFor="delivery-endpoint-filter">Delivery endpoint</label>
                <select
                  id="delivery-endpoint-filter"
                  value={deliveryFilterDraft.endpointId}
                  onChange={(event) =>
                    setDeliveryFilterDraft((current) => ({
                      ...current,
                      endpointId: event.target.value,
                    }))
                  }
                  disabled={historyStatus.phase === "loading"}
                >
                  <option value="">All endpoints</option>
                  {endpoints.map((endpoint) => (
                    <option key={endpoint.id} value={endpoint.id}>
                      {endpoint.name}
                    </option>
                  ))}
                </select>
              </div>
              <div className="field field--compact field--wide">
                <label htmlFor="delivery-event-filter">Delivery event type</label>
                <input
                  id="delivery-event-filter"
                  value={deliveryFilterDraft.eventType}
                  onChange={(event) =>
                    setDeliveryFilterDraft((current) => ({
                      ...current,
                      eventType: event.target.value,
                    }))
                  }
                  maxLength={100}
                  placeholder="file.processed"
                  disabled={historyStatus.phase === "loading"}
                />
              </div>
              <div className="filterActions">
                <button
                  className="button button--secondary button--small"
                  type="submit"
                  disabled={historyStatus.phase === "loading"}
                >
                  Apply filters
                </button>
                <button
                  className="button button--secondary button--small"
                  type="button"
                  onClick={() => void resetDeliveryFilters()}
                  disabled={
                    historyStatus.phase === "loading"
                    || (!hasDeliveryFilters(deliveryFilterDraft)
                      && !hasDeliveryFilters(appliedDeliveryFilters))
                  }
                >
                  Reset
                </button>
              </div>
            </form>
            <StatusMessage status={historyStatus} />
            {deliveries.length > 0 ? (
              <ul className="deliveryList">
                {deliveries.map((delivery) => (
                  <li key={delivery.id}>
                    <button
                      type="button"
                      className="deliveryButton"
                      aria-pressed={selectedDeliveryId === delivery.id}
                      onClick={() => void selectDelivery(delivery.id)}
                    >
                      <span className="deliveryButton__topline">
                        <strong>{delivery.eventType ?? "Synthetic event"}</strong>
                        <StateBadge state={delivery.state} />
                      </span>
                      <span className="deliveryButton__endpoint">
                        {delivery.endpointName ?? "Endpoint unavailable"}
                      </span>
                      <code>{delivery.id}</code>
                      <span>
                        {formatUtc(
                          delivery.completedAtUtc ??
                            delivery.startedAtUtc ??
                            delivery.createdAtUtc,
                        )}{" "}
                        UTC
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : null}
            {nextDeliveryCursor ? (
              <div className="historyPagination">
                <button
                  className="button button--secondary button--small"
                  type="button"
                  onClick={() => void loadMoreDeliveries()}
                  disabled={historyStatus.phase === "loading"}
                >
                  {historyStatus.phase === "loading"
                    ? "Loading…"
                    : "Load more"}
                </button>
              </div>
            ) : deliveries.length > 0 ? (
              <p className="paginationEnd">End of history.</p>
            ) : null}
          </section>
        </aside>
      </div>

      <section className="detailPanel" aria-labelledby="detail-title">
        <div className="sectionHeading">
          <div>
            <h2 id="detail-title">Attempts</h2>
          </div>
          {deliveryDetail ? <StateBadge state={deliveryDetail.state} /> : null}
        </div>
        <StatusMessage status={detailStatus} />
        {replayStatus.phase !== "idle" ? (
          <StatusMessage status={replayStatus} />
        ) : null}

        {deliveryDetail ? (
          <div className="detailContent">
            <dl className="detailMeta">
              <div>
                <dt>Delivery ID</dt>
                <dd>{deliveryDetail.id}</dd>
              </div>
              <div>
                <dt>Event ID</dt>
                <dd>{deliveryDetail.eventId ?? "Unavailable"}</dd>
              </div>
              <div>
                <dt>Correlation ID</dt>
                <dd>{deliveryDetail.correlationId ?? "Unavailable"}</dd>
              </div>
              <div>
                <dt>Latest activity</dt>
                <dd>
                  {formatUtc(
                    deliveryDetail.completedAtUtc ??
                      deliveryDetail.startedAtUtc ??
                      deliveryDetail.createdAtUtc,
                  )}{" "}
                  UTC
                </dd>
              </div>
              {deliveryDetail.attemptCount !== undefined ? (
                <div>
                  <dt>Attempts</dt>
                  <dd>
                    {deliveryDetail.attemptCount}
                    {deliveryDetail.maxAttempts
                      ? ` / ${deliveryDetail.maxAttempts}`
                      : ""}
                  </dd>
                </div>
              ) : null}
              {deliveryDetail.nextAttemptAtUtc ? (
                <div>
                  <dt>Next attempt due</dt>
                  <dd>{formatUtc(deliveryDetail.nextAttemptAtUtc)} UTC</dd>
                </div>
              ) : null}
              {deliveryDetail.replayOfDeliveryId ? (
                <div>
                  <dt>Replay of</dt>
                  <dd>{deliveryDetail.replayOfDeliveryId}</dd>
                </div>
              ) : null}
            </dl>

            {deliveryDetail.state.toLowerCase() === "failed" ? (
              <div className="replaySection">
                <button
                  type="button"
                  className="button button--secondary button--small"
                  onClick={() => void replayDelivery()}
                  disabled={replayStatus.phase === "loading" || replayBlocked}
                >
                  {replayStatus.phase === "loading"
                    ? "Scheduling replay…"
                    : "Replay delivery"}
                </button>
                {replayBlocked ? (
                  <span className="fieldHint">
                    Reactivate the endpoint before scheduling a replay.
                  </span>
                ) : null}
              </div>
            ) : null}

            {deliveryDetail.attempts.length === 0 ? (
              <p className="emptyState">No delivery attempts are recorded yet.</p>
            ) : (
              <ol className="attemptList">
                {deliveryDetail.attempts.map((attempt) => (
                  <li key={attempt.id} className="attemptCard">
                    <div className="attemptHeading">
                      <h3>Attempt {attempt.number}</h3>
                      {attempt.state ? <StateBadge state={attempt.state} /> : null}
                    </div>
                    <dl className="attemptMeta">
                      <div>
                        <dt>HTTP status</dt>
                        <dd>{attempt.statusCode ?? "Unavailable"}</dd>
                      </div>
                      <div>
                        <dt>Started</dt>
                        <dd>{formatUtc(attempt.startedAtUtc)} UTC</dd>
                      </div>
                      <div>
                        <dt>Completed</dt>
                        <dd>{formatUtc(attempt.completedAtUtc)} UTC</dd>
                      </div>
                      <div>
                        <dt>Duration</dt>
                        <dd>
                          {attempt.durationMilliseconds === undefined
                            ? "Unavailable"
                            : `${attempt.durationMilliseconds} ms`}
                        </dd>
                      </div>
                    </dl>
                    {attempt.error ? (
                      <p className="attemptError"><strong>Error:</strong> {attempt.error}</p>
                    ) : null}
                    {attempt.responseBody ? (
                      <div className="responseBlock">
                        <h4>Response body</h4>
                        <pre>{attempt.responseBody}</pre>
                      </div>
                    ) : null}
                  </li>
                ))}
              </ol>
            )}
          </div>
        ) : null}
      </section>
    </main>
  );
}
