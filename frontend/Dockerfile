# syntax=docker/dockerfile:1.7
# Multi-stage build for the ClipForge web app (Next.js standalone output).

FROM node:24-alpine AS base
RUN corepack enable && corepack prepare pnpm@9.1.3 --activate
WORKDIR /app

# ---- dependencies -----------------------------------------------------------
FROM base AS deps
COPY package.json pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

# ---- build --------------------------------------------------------------------
FROM base AS builder
# NEXT_PUBLIC_* values are inlined into the client bundle at build time.
# `{host}` = the host name the page is opened with (see .env.local.example).
ARG NEXT_PUBLIC_API_BASE_URL=http://{host}:5080
ARG NEXT_PUBLIC_BRAND_NAME=ClipForge
# Optional. Empty values hide the support links, keep the legal pages unpublished and mark the
# OBS companion as "em desenvolvimento" (see .env.local.example).
ARG NEXT_PUBLIC_SUPPORT_EMAIL=
ARG NEXT_PUBLIC_LEGAL_COMPANY_NAME=
ARG NEXT_PUBLIC_LEGAL_CNPJ=
ARG NEXT_PUBLIC_LEGAL_ADDRESS=
ARG NEXT_PUBLIC_LEGAL_FORUM=
ARG NEXT_PUBLIC_LEGAL_DPO_EMAIL=
ARG NEXT_PUBLIC_LEGAL_UPDATED_AT=
ARG NEXT_PUBLIC_COMPANION_DOWNLOAD_URL=
# Server-side auth guard on the API's cf_access cookie (default true). Set to false when the web
# host never receives that cookie (API on another host without Auth__CookieDomain).
ARG NEXT_PUBLIC_COOKIE_GUARD=true
ENV NEXT_PUBLIC_API_BASE_URL=$NEXT_PUBLIC_API_BASE_URL \
    NEXT_PUBLIC_BRAND_NAME=$NEXT_PUBLIC_BRAND_NAME \
    NEXT_PUBLIC_SUPPORT_EMAIL=$NEXT_PUBLIC_SUPPORT_EMAIL \
    NEXT_PUBLIC_LEGAL_COMPANY_NAME=$NEXT_PUBLIC_LEGAL_COMPANY_NAME \
    NEXT_PUBLIC_LEGAL_CNPJ=$NEXT_PUBLIC_LEGAL_CNPJ \
    NEXT_PUBLIC_LEGAL_ADDRESS=$NEXT_PUBLIC_LEGAL_ADDRESS \
    NEXT_PUBLIC_LEGAL_FORUM=$NEXT_PUBLIC_LEGAL_FORUM \
    NEXT_PUBLIC_LEGAL_DPO_EMAIL=$NEXT_PUBLIC_LEGAL_DPO_EMAIL \
    NEXT_PUBLIC_LEGAL_UPDATED_AT=$NEXT_PUBLIC_LEGAL_UPDATED_AT \
    NEXT_PUBLIC_COMPANION_DOWNLOAD_URL=$NEXT_PUBLIC_COMPANION_DOWNLOAD_URL \
    NEXT_PUBLIC_COOKIE_GUARD=$NEXT_PUBLIC_COOKIE_GUARD \
    NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN pnpm build

# ---- runtime ------------------------------------------------------------------
FROM node:24-alpine AS runner
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0
WORKDIR /app
RUN addgroup --system --gid 1001 nodejs \
 && adduser --system --uid 1001 --ingroup nodejs nextjs
COPY --from=builder --chown=nextjs:nodejs /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static
USER nextjs
EXPOSE 3000
CMD ["node", "server.js"]
