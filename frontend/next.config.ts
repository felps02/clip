import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Minimal server bundle for the Docker image (see Dockerfile).
  output: "standalone",
  cacheComponents: true,
  partialPrefetching: true,
  // Thumbnails and posters come from short-lived signed URLs on arbitrary hosts
  // (local API or S3-compatible storage); the browser loads them directly.
  images: {
    unoptimized: true,
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
