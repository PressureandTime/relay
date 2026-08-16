import { RelayDashboard } from "./components/relay-dashboard";
import { loadEndpoints, loadRecentDeliveries } from "@/lib/server-api";

export const dynamic = "force-dynamic";

export default async function Home() {
  const [endpoints, deliveries] = await Promise.all([
    loadEndpoints(),
    loadRecentDeliveries(),
  ]);

  return (
    <RelayDashboard
      initialEndpoints={endpoints.data}
      initialEndpointError={endpoints.error}
      initialDeliveries={deliveries.data.items}
      initialNextDeliveryCursor={deliveries.data.nextCursor}
      initialDeliveryError={deliveries.error}
    />
  );
}
