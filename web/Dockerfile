# Панель для локального стенда: сборка Vite и vite preview; /api и /hubs проксируются на сервис (AEZA_API).
FROM node:24.22.1-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY index.html tsconfig.json vite.config.ts ./
COPY public ./public
COPY src ./src
RUN npm run build

FROM node:24.22.1-alpine
WORKDIR /web
ENV NODE_ENV=production \
    AEZA_API=http://app:5000
COPY --from=build /web/node_modules ./node_modules
COPY --from=build /web/dist ./dist
COPY --from=build /web/package.json /web/vite.config.ts ./
# Vite пишет временные файлы в node_modules/.vite-temp
RUN mkdir -p node_modules/.vite-temp && chown node:node /web node_modules/.vite-temp
USER node
EXPOSE 8080
CMD ["npx", "vite", "preview", "--host", "0.0.0.0", "--port", "8080", "--strictPort"]
