import type { NextConfig } from "next";

const apiBaseUrl = process.env.RELAY_API_BASE_URL ?? "http://api:8080";
const receiverBaseUrl =
  process.env.RELAY_RECEIVER_BASE_URL ?? "http://receiver:8080";

const nextConfig: NextConfig = {
  async rewrites() {
    return [
      {
        source: "/relay-api/:path*",
        destination: `${apiBaseUrl}/api/:path*`,
      },
      {
        source: "/receiver-control/:path*",
        destination: `${receiverBaseUrl}/_control/:path*`,
      },
    ];
  },
};

export default nextConfig;
