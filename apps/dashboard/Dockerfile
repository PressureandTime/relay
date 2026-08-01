FROM node:24.18.1-bookworm-slim AS build
WORKDIR /workspace
COPY package.json package-lock.json ./
COPY apps/dashboard/package.json apps/dashboard/package.json
COPY tests/e2e/package.json tests/e2e/package.json
RUN npm ci
COPY apps/dashboard apps/dashboard
RUN npm run build --workspace @relay/dashboard

FROM node:24.18.1-bookworm-slim AS runtime
WORKDIR /workspace
ENV NODE_ENV=production
ENV HOSTNAME=0.0.0.0
ENV PORT=3000
COPY --from=build --chown=node:node /workspace/package.json ./package.json
COPY --from=build --chown=node:node /workspace/package-lock.json ./package-lock.json
COPY --from=build --chown=node:node /workspace/node_modules ./node_modules
COPY --from=build --chown=node:node /workspace/apps/dashboard ./apps/dashboard
USER node
EXPOSE 3000
CMD ["npm", "run", "start", "--workspace", "@relay/dashboard"]
