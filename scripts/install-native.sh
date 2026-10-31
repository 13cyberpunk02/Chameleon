#!/usr/bin/env bash
#
# Chameleon: установка сервера прямо на VPS (без Docker), как служба systemd.
# Подходит для Debian и Ubuntu. Запускать от root:
#
#   curl -fsSL https://raw.githubusercontent.com/13cyberpunk02/Chameleon/master/scripts/install-native.sh | sudo bash
#
# Повторный запуск обновляет сервер: ключи, клиенты и настройки сохраняются.
#
#   --yes         не задавать вопросов (значения по умолчанию или из переменных ниже)
#   --no-panel    не ставить веб-панель (не нужны nginx и сборка на Node.js)
#   --uninstall   удалить сервер; данные в /var/lib/chameleon остаются
#
# Настройки без вопросов: CHAMELEON_ADDRESS, CHAMELEON_PORT, CHAMELEON_SNI, CHAMELEON_DECOY,
# CHAMELEON_CLIENT_NAME; источник кода: CHAMELEON_REPO, CHAMELEON_BRANCH.

set -euo pipefail

REPO_URL="${CHAMELEON_REPO:-https://github.com/13cyberpunk02/Chameleon.git}"
BRANCH="${CHAMELEON_BRANCH:-master}"
INSTALL_DIR=/opt/chameleon
DATA_DIR=/var/lib/chameleon
CONFIG_DIR=/etc/chameleon
ENV_FILE="$CONFIG_DIR/server.env"
LINK_FILE="$CONFIG_DIR/first-client.txt"
SERVICE=chameleon
SERVICE_USER=chameleon
NGINX_SITE=/etc/nginx/conf.d/chameleon-panel.conf
API_URL=http://127.0.0.1:9090
PANEL_PORT=8080
MIN_MEMORY_MB=2048

ASSUME_YES=0
WITH_PANEL=1
UNINSTALL=0
for arg in "$@"; do
  case "$arg" in
    --yes) ASSUME_YES=1 ;;
    --no-panel) WITH_PANEL=0 ;;
    --uninstall) UNINSTALL=1 ;;
    *) echo "Неизвестный параметр: $arg" >&2; exit 2 ;;
  esac
done

info() { printf '\n\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mВнимание:\033[0m %s\n' "$*"; }
die() { printf '\n\033[1;31mОшибка:\033[0m %s\n' "$*" >&2; exit 1; }

# ask ПЕРЕМЕННАЯ "Вопрос" "значение по умолчанию". Заранее заданная переменная побеждает.
ask() {
  local var="$1" question="$2" default="$3" answer=""
  if [ -n "${!var:-}" ]; then return; fi
  if [ "$ASSUME_YES" = 0 ] && [ -r /dev/tty ]; then
    if [ -n "$default" ]; then
      read -r -p "$question [$default]: " answer </dev/tty || true
    else
      read -r -p "$question: " answer </dev/tty || true
    fi
  fi
  printf -v "$var" '%s' "${answer:-$default}"
}

uninstall() {
  info "Удаляю сервер Chameleon"
  systemctl disable --now "$SERVICE" 2>/dev/null || true
  rm -f "/etc/systemd/system/$SERVICE.service"
  systemctl daemon-reload
  if [ -f "$NGINX_SITE" ]; then
    rm -f "$NGINX_SITE"
    systemctl reload nginx 2>/dev/null || true
  fi
  rm -rf "$INSTALL_DIR"
  info "Готово. Ключи и база клиентов остались в $DATA_DIR и $CONFIG_DIR: удалите их вручную, если они не нужны."
}

check_system() {
  [ "$(id -u)" = 0 ] || die "Нужны права root. Запустите через sudo."
  command -v apt-get >/dev/null || die "Этот скрипт работает на Debian и Ubuntu. На другой системе используйте install-docker.sh."
  command -v systemctl >/dev/null || die "Нужен systemd. Используйте install-docker.sh."
  case "$(uname -m)" in
    x86_64) NODE_ARCH=x64 ;;
    aarch64) NODE_ARCH=arm64 ;;
    *) die "Процессор $(uname -m) не поддерживается: нужен x86_64 или aarch64." ;;
  esac
}

install_packages() {
  info "Устанавливаю системные пакеты"
  export DEBIAN_FRONTEND=noninteractive
  NGINX_WAS_INSTALLED=0
  if command -v nginx >/dev/null; then NGINX_WAS_INSTALLED=1; fi
  local packages=(ca-certificates curl git openssl iproute2 procps libssl3 qrencode xz-utils tar)
  [ "$WITH_PANEL" = 1 ] && packages+=(nginx)
  apt-get update -qq
  # libssl3 называется иначе на старых системах: отсутствие этого имени не должно ломать установку.
  apt-get install -y -qq "${packages[@]}" >/dev/null ||
    apt-get install -y -qq "${packages[@]/libssl3/libssl1.1}" >/dev/null
}

# Сборка требует больше памяти, чем есть на маленьком VPS: без подкачки её убьёт OOM killer.
ensure_memory() {
  local total_mb file=/swapfile
  total_mb=$(free -m | awk '/^Mem:/ {m=$2} /^Swap:/ {s=$2} END {print m+s}')
  if [ "$total_mb" -ge "$MIN_MEMORY_MB" ] || [ -e "$file" ]; then return; fi
  info "Памяти мало (${total_mb} МБ): создаю файл подкачки 2 ГБ"
  if fallocate -l 2G "$file" 2>/dev/null || dd if=/dev/zero of="$file" bs=1M count=2048 status=none; then
    chmod 600 "$file"
    if mkswap "$file" >/dev/null && swapon "$file"; then
      grep -q "^$file " /etc/fstab || echo "$file none swap sw 0 0" >>/etc/fstab
      return
    fi
  fi
  rm -f "$file"
  warn "Не удалось создать подкачку: сборке может не хватить памяти."
}

detect_address() {
  curl -4 -fsS --max-time 5 https://api.ipify.org 2>/dev/null ||
    curl -4 -fsS --max-time 5 https://ifconfig.me 2>/dev/null ||
    hostname -I 2>/dev/null | awk '{print $1}' || true
}

port_is_free() {
  [ -z "$(ss -ltnH "sport = :$1" 2>/dev/null)" ]
}

configure() {
  # При обновлении берём то, что выбрали в прошлый раз.
  if [ -f "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    . "$ENV_FILE"
    CHAMELEON_ADDRESS="${CHAMELEON_ADDRESS:-${CHAMELEON_PUBLIC%:*}}"
    CHAMELEON_PORT="${CHAMELEON_PORT:-${CHAMELEON_LISTEN##*:}}"
    return
  fi

  info "Несколько вопросов (Enter - значение в скобках)"
  ask CHAMELEON_ADDRESS "Адрес этого сервера (IP или домен) для клиентов" "$(detect_address)"
  [ -n "$CHAMELEON_ADDRESS" ] || die "Не удалось определить адрес сервера. Задайте его: CHAMELEON_ADDRESS=1.2.3.4"
  ask CHAMELEON_PORT "Порт (443 выглядит как обычный HTTPS)" 443
  case "$CHAMELEON_PORT" in
    '' | *[!0-9]*) die "Порт должен быть числом: $CHAMELEON_PORT" ;;
  esac
  port_is_free "$CHAMELEON_PORT" || die "Порт $CHAMELEON_PORT уже занят: $(ss -ltnpH "sport = :$CHAMELEON_PORT" | head -1). Освободите его или выберите другой."
  ask CHAMELEON_SNI "Домен маскировки (SNI)" www.microsoft.com
  ask CHAMELEON_DECOY "Сайт-прикрытие для посторонних, host:80, без TLS (Enter - простая заглушка)" ""
  ask CHAMELEON_CLIENT_NAME "Имя первого клиента" "Мой клиент"
  CHAMELEON_API_TOKEN="$(openssl rand -hex 32)"

  mkdir -p "$CONFIG_DIR"
  install -m 600 /dev/null "$ENV_FILE"
  cat >"$ENV_FILE" <<EOF
CHAMELEON_LISTEN=0.0.0.0:$CHAMELEON_PORT
CHAMELEON_PUBLIC=$CHAMELEON_ADDRESS:$CHAMELEON_PORT
CHAMELEON_SNI=$CHAMELEON_SNI
CHAMELEON_DECOY=$CHAMELEON_DECOY
CHAMELEON_ALLOWLIST=1
CHAMELEON_KEY_FILE=$DATA_DIR/server.key
CHAMELEON_DB=$DATA_DIR/chameleon.db
CHAMELEON_CLIENTS_FILE=$DATA_DIR/clients.json
CHAMELEON_TRAFFIC_FILE=$DATA_DIR/traffic.json
CHAMELEON_API_TOKEN=$CHAMELEON_API_TOKEN
CHAMELEON_API_LISTEN=$API_URL/
CHAMELEON_API_TRUST_PROXY=1
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
DOTNET_ROOT=$INSTALL_DIR/dotnet
EOF
}

get_source() {
  local script_dir=""
  # При запуске через curl | bash файла скрипта нет, а из клона репозитория код уже на месте.
  if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  fi
  if [ -n "$script_dir" ] && [ -f "$script_dir/../Chameleon.slnx" ]; then
    SOURCE_DIR="$(cd "$script_dir/.." && pwd)"
    info "Использую код из $SOURCE_DIR"
    return
  fi
  SOURCE_DIR="$INSTALL_DIR/src"
  if [ -d "$SOURCE_DIR/.git" ]; then
    info "Обновляю код"
    git -C "$SOURCE_DIR" fetch -q --depth 1 origin "$BRANCH"
    git -C "$SOURCE_DIR" reset -q --hard FETCH_HEAD
  else
    info "Скачиваю код из $REPO_URL"
    mkdir -p "$INSTALL_DIR"
    git clone -q --depth 1 --branch "$BRANCH" "$REPO_URL" "$SOURCE_DIR"
  fi
}

build_server() {
  info "Устанавливаю .NET 10"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROOT="$INSTALL_DIR/dotnet"
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_CLI_HOME="$INSTALL_DIR/.dotnet-home"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT" >/dev/null
  rm -f /tmp/dotnet-install.sh

  info "Собираю сервер (несколько минут)"
  rm -rf "$INSTALL_DIR/server.new"
  "$DOTNET_ROOT/dotnet" publish "$SOURCE_DIR/src/Chameleon.Server/Chameleon.Server.csproj" \
    -c Release -o "$INSTALL_DIR/server.new" /p:UseAppHost=false -v quiet --nologo >/dev/null
}

build_panel() {
  [ "$WITH_PANEL" = 1 ] || return 0
  info "Собираю веб-панель (несколько минут)"
  local tmp ui node_file
  tmp="$(mktemp -d)"
  node_file="$(curl -fsSL "https://nodejs.org/dist/latest-v24.x/" |
    grep -oE "node-v[0-9.]+-linux-$NODE_ARCH\.tar\.xz" | head -1)"
  [ -n "$node_file" ] || die "Не удалось найти Node.js для сборки панели. Установите без неё: --no-panel"
  curl -fsSL "https://nodejs.org/dist/latest-v24.x/$node_file" | tar -xJ -C "$tmp" --strip-components=1
  ui="$tmp/ui"
  cp -a "$SOURCE_DIR/src/Chameleon.UI" "$ui"
  rm -rf "$ui/node_modules" "$ui/dist"
  (
    cd "$ui"
    export PATH="$tmp/bin:$PATH"
    npm ci --no-audit --no-fund --no-update-notifier --loglevel=error >/dev/null
    npx ng build >/dev/null
  )
  rm -rf "$INSTALL_DIR/panel"
  mkdir -p "$INSTALL_DIR"
  cp -a "$ui/dist/Chameleon.UI/browser" "$INSTALL_DIR/panel"

  # Тот же nginx.conf, что в Docker, но API здесь локальный, а панель не слушает внешние адреса.
  sed -e "s|listen 8080;|listen 127.0.0.1:$PANEL_PORT;|" \
      -e "s|proxy_pass http://chameleon:9090;|proxy_pass $API_URL;|" \
      -e "s|root /usr/share/nginx/html;|root $INSTALL_DIR/panel;|" \
      "$ui/nginx.conf" >"$NGINX_SITE"
  rm -rf "$tmp"

  # Страница-приветствие nginx на порту 80 выдавала бы сервер: убираем, если nginx поставили мы.
  if [ "$NGINX_WAS_INSTALLED" = 0 ]; then rm -f /etc/nginx/sites-enabled/default; fi
  nginx -t -q || die "nginx отклонил настройку панели, см. $NGINX_SITE"
  systemctl enable -q nginx
  systemctl restart nginx
}

install_service() {
  info "Запускаю службу $SERVICE"
  id "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
  mkdir -p "$DATA_DIR"
  chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
  chmod 700 "$DATA_DIR"

  cat >"/etc/systemd/system/$SERVICE.service" <<EOF
[Unit]
Description=Chameleon server
After=network-online.target
Wants=network-online.target

[Service]
User=$SERVICE_USER
EnvironmentFile=$ENV_FILE
ExecStart=$INSTALL_DIR/dotnet/dotnet $INSTALL_DIR/server/Chameleon.Server.dll
Restart=always
RestartSec=3
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DATA_DIR
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl stop "$SERVICE" 2>/dev/null || true
  rm -rf "$INSTALL_DIR/server"
  mv "$INSTALL_DIR/server.new" "$INSTALL_DIR/server"
  systemctl enable -q "$SERVICE"
  systemctl start "$SERVICE"

  local i
  for i in $(seq 1 30); do
    if curl -fsS -o /dev/null -H "Authorization: Bearer $CHAMELEON_API_TOKEN" "$API_URL/api/server" 2>/dev/null; then
      return
    fi
    sleep 1
  done
  journalctl -u "$SERVICE" -n 30 --no-pager >&2 || true
  die "Сервер не запустился за 30 секунд, журнал выше."
}

open_firewall() {
  if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q '^Status: active'; then
    info "Открываю порт $CHAMELEON_PORT в ufw"
    ufw allow "$CHAMELEON_PORT/tcp" >/dev/null
  fi
}

# Ссылка с личным ключом существует только в ответе API, поэтому сохраняем её один раз.
create_first_client() {
  # Проверяем файл, а не факт обновления: первая установка могла оборваться до создания клиента.
  if [ -s "$LINK_FILE" ]; then return; fi
  info "Создаю первого клиента"
  local name response link
  name="${CHAMELEON_CLIENT_NAME:-Мой клиент}"
  name="${name//\\/}"
  name="${name//\"/}"
  response="$(curl -fsS -H "Authorization: Bearer $CHAMELEON_API_TOKEN" -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\"}" "$API_URL/api/clients/generate")" || die "Не удалось создать клиента."
  link="$(printf '%s' "$response" | sed -n 's/.*"link":"\([^"]*\)".*/\1/p')"
  [ -n "$link" ] || die "Сервер вернул неожиданный ответ при создании клиента."
  install -m 600 /dev/null "$LINK_FILE"
  # JSON кодирует & как &.
  printf '%b\n' "${link//\\u00/\\x}" >"$LINK_FILE"
}

print_summary() {
  local address port
  address="${CHAMELEON_PUBLIC:-$CHAMELEON_ADDRESS:$CHAMELEON_PORT}"
  port="${address##*:}"
  printf '\n\033[1;32m✔ Сервер Chameleon работает на %s\033[0m\n' "$address"
  if [ -s "$LINK_FILE" ]; then
    printf '\nСсылка для первого клиента. В ней личный ключ: не публикуйте её.\n\n%s\n\n' "$(cat "$LINK_FILE")"
    command -v qrencode >/dev/null && qrencode -t ANSIUTF8 "$(cat "$LINK_FILE")" || true
    printf 'Ссылка сохранена в %s\n' "$LINK_FILE"
  fi
  if [ "$WITH_PANEL" = 1 ]; then
    cat <<EOF

Веб-панель. Выполните на своём компьютере и оставьте окно открытым:
  ssh -L $PANEL_PORT:127.0.0.1:$PANEL_PORT root@${address%:*}
Затем откройте http://127.0.0.1:$PANEL_PORT и введите токен:
  $CHAMELEON_API_TOKEN
EOF
  fi
  cat <<EOF

Проверьте, что порт $port открыт у хостера, если клиент не подключается.
Журнал сервера: journalctl -u $SERVICE -f
Перезапуск:     systemctl restart $SERVICE
Настройки:      $ENV_FILE
EOF
}

main() {
  if [ "$UNINSTALL" = 1 ]; then
    [ "$(id -u)" = 0 ] || die "Нужны права root. Запустите через sudo."
    uninstall
    exit 0
  fi
  check_system
  install_packages
  ensure_memory
  # При обновлении порт занят самим сервером, поэтому настройки читаем до его остановки.
  configure
  get_source
  build_server
  build_panel
  install_service
  open_firewall
  create_first_client
  print_summary
}

main
