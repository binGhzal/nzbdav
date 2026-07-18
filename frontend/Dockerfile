# Build the project
FROM node:24-alpine3.23@sha256:595398b0081eacda8e1c4c5b97b76cd1020e4d58a8ebcb4843b9bca1e79e7436 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN npm ci
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# Build the production image
FROM node:24-alpine3.23@sha256:595398b0081eacda8e1c4c5b97b76cd1020e4d58a8ebcb4843b9bca1e79e7436
EXPOSE 3000
WORKDIR /nzb-webdav
ENV NODE_ENV=production
COPY --from=build-env /nzb-webdav/node_modules ./node_modules
COPY --from=build-env /nzb-webdav/package.json ./package.json
COPY --from=build-env /nzb-webdav/dist-node/server.js ./dist-node/server.js
COPY --from=build-env /nzb-webdav/build ./build
CMD ["npm", "run", "start"]
