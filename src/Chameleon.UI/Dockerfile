# --- сборка ---
FROM node:24-alpine AS build
WORKDIR /ui
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx ng build

# --- раздача статики и прокси к API ---
FROM nginx:1.29-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /ui/dist/Chameleon.UI/browser /usr/share/nginx/html
EXPOSE 8080
