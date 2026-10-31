# Панель Chameleon

Веб-панель сервера на Angular: сессии, трафик, клиенты и их лимиты. Развёртывание на
VPS описано в [DEPLOY.md](../../DEPLOY.md).

## Разработка

Запустите сервер с включённым API, затем панель:

```bash
CHAMELEON_API_TOKEN=dev CHAMELEON_API_LISTEN=http://127.0.0.1:9090/ dotnet run --project ../Chameleon.Server
npm install
npm start
```

Панель откроется на `http://localhost:4200`, токен для входа - `dev`. Запросы `/api`
проксируются на `127.0.0.1:9090` (см. `proxy.conf.json`).

## Сборка и тесты

```bash
npm run build
npm test -- --watch=false
```

В рабочей сборке панель и API живут на одном origin: CORS на сервере не включён.
Заголовки безопасности (CSP и другие) задаёт `nginx.conf`. В `angular.json` отключён
`inlineCritical`: он подключает стили через инлайновый `onload`, который запрещает CSP.
