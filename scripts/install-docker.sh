#!/usr/bin/env bash
#
# Chameleon: установка сервера и веб-панели в Docker. Запускать от root:
#
#   curl -fsSL https://raw.githubusercontent.com/13cyberpunk02/Chameleon/master/scripts/install-docker.sh | sudo bash
#
# Если Docker нет, скрипт поставит его сам. Повторный запуск обновляет сервер:
# ключи, клиенты и настройки сохраняются.
#
#   --yes         не задавать вопросов (значения по умолчанию или из переменных ниже)
#   --uninstall   остановить и удалить контейнеры; данные в томе Docker остаются
#
# Настройки без вопросов: CHAMELEON_ADDRESS, CHAMELEON_PORT, CHAMELEON_SNI, CHAMELEON_DECOY,
# CHAMELEON_CLIENT_NAME; источник кода: CHAMELEON_REPO, CHAMELEON_BRANCH.

set -euo pipefail

REPO_URL="${CHAMELEON_REPO:-https://github.com/13cyberpunk02/Chameleon.git}"
BRANCH="${CHAMELEON_BRANCH:-master}"
INSTALL_DIR=/opt/chameleon
CONFIG_DIR=/etc/chameleon
ENV_FILE="$CONFIG_DIR/docker.env"
LINK_FILE="$CONFIG_DIR/first-client.txt"
SERVER_CONTAINER=chameleon
PANEL_CONTAINER=chameleon-panel
API_URL=http://chameleon:9090
PANEL_PORT=8080
MIN_MEMORY_MB=2048

ASSUME_YES=0
UNINSTALL=0
for arg in "$@"; do
  case "$arg" in
    --yes) ASSUME_YES=1 ;;
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

find_source_dir() {
  local script_dir=""
  # При запуске через curl | bash файла скрипта нет, а из клона репозитория код уже на месте.
  if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  fi
  if [ -n "$script_dir" ] && [ -f "$script_dir/../Chameleon.slnx" ]; then
    SOURCE_DIR="$(cd "$script_dir/.." && pwd)"
    LOCAL_SOURCE=1
  else
    SOURCE_DIR="$INSTALL_DIR/src"
    LOCAL_SOURCE=0
  fi
}

compose() {
  docker compose --project-name chameleon --project-directory "$SOURCE_DIR" \
    -f "$SOURCE_DIR/docker-compose.yml" --env-file "$ENV_FILE" --profile panel "$@"
}

uninstall() {
  info "Удаляю контейнеры Chameleon"
  find_source_dir
  if [ -f "$ENV_FILE" ] && [ -f "$SOURCE_DIR/docker-compose.yml" ]; then
    compose down
  else
    docker rm -f "$SERVER_CONTAINER" "$PANEL_CONTAINER" 2>/dev/null || true
  fi
  rm -rf "$INSTALL_DIR"
  info "Готово. Ключи и база клиентов остались в томе chameleon_chameleon-data, настройки в $CONFIG_DIR. Полное удаление: docker volume rm chameleon_chameleon-data && rm -rf $CONFIG_DIR"
}

ensure_tools() {
  local missing=()
  command -v curl >/dev/null || missing+=(curl)
  command -v git >/dev/null || missing+=(git)
  command -v openssl >/dev/null || missing+=(openssl)
  command -v ss >/dev/null || missing+=(iproute2)
  command -v free >/dev/null || missing+=(procps)
  command -v qrencode >/dev/null || missing+=(qrencode)
  [ "${#missing[@]}" -gt 0 ] || return 0
  info "Устанавливаю: ${missing[*]}"
  if command -v apt-get >/dev/null; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq ca-certificates "${missing[@]}" >/dev/null
  elif command -v dnf >/dev/null; then
    # На этих системах пакет называется iproute, а QR-код необязателен.
    local names=("${missing[@]/iproute2/iproute}")
    dnf install -y -q "${names[@]/qrencode/}" >/dev/null
  else
    die "Установите вручную: ${missing[*]}"
  fi
}

ensure_docker() {
  if ! command -v docker >/dev/null; then
    info "Устанавливаю Docker"
    curl -fsSL https://get.docker.com | sh >/dev/null
  fi
  command -v systemctl >/dev/null && systemctl enable --now docker >/dev/null 2>&1 || true
  docker info >/dev/null 2>&1 || die "Docker установлен, но не запущен: проверьте systemctl status docker."
  docker compose version >/dev/null 2>&1 || die "Нет плагина docker compose. Установите его: https://docs.docker.com/compose/install/linux/"
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
  port_is_free "$PANEL_PORT" || die "Порт панели $PANEL_PORT уже занят: $(ss -ltnpH "sport = :$PANEL_PORT" | head -1). Освободите его."
  ask CHAMELEON_SNI "Домен маскировки (SNI)" www.microsoft.com
  ask CHAMELEON_DECOY "Сайт-прикрытие для посторонних, host:80, без TLS (Enter - простая заглушка)" ""
  ask CHAMELEON_CLIENT_NAME "Имя первого клиента" "Мой клиент"
  CHAMELEON_API_TOKEN="$(openssl rand -hex 32)"

  mkdir -p "$CONFIG_DIR"
  install -m 600 /dev/null "$ENV_FILE"
  cat >"$ENV_FILE" <<EOF
CHAMELEON_PORT=$CHAMELEON_PORT
CHAMELEON_PUBLIC=$CHAMELEON_ADDRESS:$CHAMELEON_PORT
CHAMELEON_SNI=$CHAMELEON_SNI
CHAMELEON_DECOY=$CHAMELEON_DECOY
CHAMELEON_ALLOWLIST=1
CHAMELEON_API_TOKEN=$CHAMELEON_API_TOKEN
EOF
  # shellcheck disable=SC1090
  . "$ENV_FILE"
}

get_source() {
  if [ "$LOCAL_SOURCE" = 1 ]; then
    info "Использую код из $SOURCE_DIR"
  elif [ -d "$SOURCE_DIR/.git" ]; then
    info "Обновляю код"
    git -C "$SOURCE_DIR" fetch -q --depth 1 origin "$BRANCH"
    git -C "$SOURCE_DIR" reset -q --hard FETCH_HEAD
  else
    info "Скачиваю код из $REPO_URL"
    mkdir -p "$INSTALL_DIR"
    git clone -q --depth 1 --branch "$BRANCH" "$REPO_URL" "$SOURCE_DIR"
  fi
}

start_containers() {
  info "Собираю и запускаю контейнеры (при первом запуске несколько минут)"
  compose up -d --build --quiet-pull

  local i
  for i in $(seq 1 30); do
    if api_get /api/server >/dev/null 2>&1; then return; fi
    sleep 1
  done
  compose logs --tail 30 "$SERVER_CONTAINER" >&2 || true
  die "Сервер не запустился за 30 секунд, журнал выше."
}

# Порт API наружу не опубликован: обращаемся к нему из контейнера панели.
api_get() {
  docker exec "$PANEL_CONTAINER" wget -qO- --header="Authorization: Bearer $CHAMELEON_API_TOKEN" "$API_URL$1"
}

api_post() {
  docker exec "$PANEL_CONTAINER" wget -qO- --header="Authorization: Bearer $CHAMELEON_API_TOKEN" \
    --header='Content-Type: application/json' --post-data="$2" "$API_URL$1"
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
  response="$(api_post /api/clients/generate "{\"name\":\"$name\"}")" || die "Не удалось создать клиента."
  link="$(printf '%s' "$response" | sed -n 's/.*"link":"\([^"]*\)".*/\1/p')"
  [ -n "$link" ] || die "Сервер вернул неожиданный ответ при создании клиента."
  install -m 600 /dev/null "$LINK_FILE"
  # JSON кодирует & как &.
  printf '%b\n' "${link//\\u00/\\x}" >"$LINK_FILE"
}

print_summary() {
  printf '\n\033[1;32m✔ Сервер Chameleon работает на %s\033[0m\n' "$CHAMELEON_PUBLIC"
  if [ -s "$LINK_FILE" ]; then
    printf '\nСсылка для первого клиента. В ней личный ключ: не публикуйте её.\n\n%s\n\n' "$(cat "$LINK_FILE")"
    command -v qrencode >/dev/null && qrencode -t ANSIUTF8 "$(cat "$LINK_FILE")" || true
    printf 'Ссылка сохранена в %s\n' "$LINK_FILE"
  fi
  cat <<EOF

Веб-панель. Выполните на своём компьютере и оставьте окно открытым:
  ssh -L $PANEL_PORT:127.0.0.1:$PANEL_PORT root@${CHAMELEON_PUBLIC%:*}
Затем откройте http://127.0.0.1:$PANEL_PORT и введите токен:
  $CHAMELEON_API_TOKEN

Проверьте, что порт ${CHAMELEON_PUBLIC##*:} открыт у хостера, если клиент не подключается.
Журнал сервера: docker logs -f $SERVER_CONTAINER
Перезапуск:     docker restart $SERVER_CONTAINER
Настройки:      $ENV_FILE
EOF
}

main() {
  [ "$(id -u)" = 0 ] || die "Нужны права root. Запустите через sudo."
  if [ "$UNINSTALL" = 1 ]; then
    uninstall
    exit 0
  fi
  find_source_dir
  ensure_tools
  ensure_docker
  ensure_memory
  configure
  get_source
  start_containers
  open_firewall
  create_first_client
  print_summary
}

main
