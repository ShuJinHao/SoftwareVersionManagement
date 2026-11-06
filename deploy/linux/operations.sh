#!/usr/bin/env bash
set -euo pipefail
umask 077
svm_root="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
svm_env="${SVM_OPERATIONS_ENV_FILE:-$svm_root/deployment.env}"
[[ "$svm_env" == "$svm_root/deployment.env" || "$svm_env" == "$svm_root/deployment.offline.env" ]] || exit 2
svm_value() { awk -F= -v key="$1" '$1 == key {print substr($0,index($0,"=")+1)}' "$svm_env"; }
svm_project="$(svm_value SVM_PROJECT)"
svm_platform="$(svm_value SVM_PLATFORM)"
svm_image="$(svm_value SVM_RUNTIME_IMAGE)"
svm_user="$(svm_value SVM_UID):$(svm_value SVM_GID)"
[[ "$svm_project" =~ ^svm-(demo|site|restore)-[a-f0-9]{12}$ && "$svm_user" =~ ^[1-9][0-9]*:[1-9][0-9]*$ ]] || { echo 'Invalid deployment identity.' >&2; exit 2; }
[[ "$svm_platform" == linux/amd64 || "$svm_platform" == linux/arm64 ]] || exit 2
svm_marker="$svm_root/.svm-deployment"; [[ -f "$svm_marker" ]] || svm_marker="$svm_root/.svm-demo"
[[ -f "$svm_marker" && "$(cat "$svm_marker")" == "$svm_project" ]] || { echo 'Deployment marker mismatch.' >&2; exit 2; }
svm_safe_path() {
  [[ "$1" == /* && "$1" != *','* && "$1" != *$'\n'* && "$1" != *$'\r'* ]] || { echo 'Absolute regular paths are required.' >&2; return 2; }
  local svm_part="$1"
  while [[ "$svm_part" != / ]]; do [[ ! -L "$svm_part" ]] || { echo 'Linked paths are refused.' >&2; return 2; }; svm_part="$(dirname -- "$svm_part")"; done
}
svm_safe_path "$svm_root"
mkdir -p "$svm_root/recovery"
mkdir "$svm_root/recovery/operation.lock" 2>/dev/null || { echo 'An operation is active or interrupted. Inspect its stopped resources before removing its lock.' >&2; exit 4; }
svm_configuration="$svm_root/recovery/operation.lock/configuration.json"
trap 'rm -f -- "$svm_configuration"; rmdir -- "$svm_root/recovery/operation.lock" 2>/dev/null || true' EXIT
svm_compose=(docker compose --env-file "$svm_env" --project-name "$svm_project" -f "$svm_root/compose.yaml")
if [[ "$(svm_value SVM_MODE)" == full ]]; then svm_compose+=(--profile full); else svm_compose+=(-f "$svm_root/compose.preview.yaml"); fi
if [[ -f "$svm_root/recovery/locked.json" ]]; then svm_compose+=(-f "$svm_root/compose.recovery.yaml" -f "$svm_root/compose.review.yaml"); fi
svm_helper() {
  [[ "$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$svm_image")" == "$svm_platform" ]] || { echo 'Runtime image platform differs from this deployment.' >&2; return 2; }
  docker run --rm --pull never --network none --user "$svm_user" --read-only --tmpfs /tmp --security-opt no-new-privileges:true \
    "$@" "$svm_image" /opt/svm/Svm.Migration/Svm.Migration.dll "${svm_helper_command[@]}"
}
svm_cold() {
  local svm_remaining
  svm_remaining="$(docker ps -q --filter "label=com.docker.compose.project=$svm_project")"
  [[ -z "$svm_remaining" ]] || { echo 'Deployment still has running writers; backup remains blocked.' >&2; return 3; }
  for svm_service in postgres rabbitmq; do
    [[ "$svm_service" != rabbitmq || "$(svm_value SVM_MODE)" == full ]] || continue
    local svm_container
    svm_container="$("${svm_compose[@]}" ps -a -q "$svm_service")"
    [[ -n "$svm_container" && "$(docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' "$svm_container")" == 'exited 0' ]] || { echo 'Database/broker did not stop cleanly; do not seal a backup.' >&2; return 3; }
  done
}
svm_archive() {
  local svm_action="$1" svm_source="$2" svm_destination="$3"
  svm_safe_path "$svm_source"; svm_safe_path "$svm_destination"
  printf '{"source":"/source","destination":"/destination","platform":"%s"}\n' "$svm_platform" > "$svm_configuration"
  svm_helper_command=("$svm_action" --config /configuration.json)
  svm_helper --mount "type=bind,src=$svm_source,dst=/source,readonly" --mount "type=bind,src=$svm_destination,dst=/destination" \
    --mount "type=bind,src=$svm_configuration,dst=/configuration.json,readonly"
}
case "${1:-}" in
  backup)
    [[ $# == 2 ]] || { echo 'Usage: ./svm.sh backup /new/private/backup-directory' >&2; exit 2; }
    svm_safe_path "$2"; [[ ! -e "$2" && "$2" != "$svm_root/"* ]] || { echo 'Choose a new directory outside this deployment.' >&2; exit 2; }
    mkdir -m 700 -- "$2"
    if [[ "$(svm_value SVM_MODE)" == full ]]; then
      "${svm_compose[@]}" stop -t 30 gateway api-a api-b
      "${svm_compose[@]}" stop -t 30 worker-a worker-b
      "${svm_compose[@]}" stop -t 60 rabbitmq postgres
    else "${svm_compose[@]}" stop -t 30 api-a; "${svm_compose[@]}" stop -t 60 postgres; fi
    svm_cold
    printf '{"project":"%s","stoppedAt":"%s","mode":"ColdSamePlatform"}\n' "$svm_project" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$svm_root/recovery/quiesced.json"
    svm_archive backup-seal "$svm_root" "$2"
    echo 'Backup verified. Source remains stopped; restart it explicitly only when appropriate.' ;;
  verify-backup)
    [[ $# == 2 && -d "$2" ]] || exit 2
    svm_archive backup-verify "$2" "$svm_root/recovery/operation.lock" ;;
  restore)
    [[ $# == 3 && -d "$2" ]] || { echo 'Usage: ./svm.sh restore /private/backup /new/isolated/deployment' >&2; exit 2; }
    svm_safe_path "$3"; [[ ! -e "$3" && "$3" != "$svm_root/"* ]] || { echo 'Restore never overwrites an existing deployment.' >&2; exit 2; }
    mkdir -m 700 -- "$3"; svm_archive backup-restore "$2" "$3"
    echo 'Restored into a new fenced deployment. No containers or external entry points were started. Build its recorded runtime artifact before review.' ;;
  diagnostics)
    [[ $# == 1 ]] || exit 2
    "${svm_compose[@]}" ps
    svm_result=0
    for svm_service in api-a api-b worker-a worker-b; do
      [[ "$svm_service" == api-a || "$(svm_value SVM_MODE)" == full ]] || continue
      svm_host=Svm.HttpApi; [[ "$svm_service" != worker-* ]] || svm_host=Svm.Worker
      "${svm_compose[@]}" exec -T "$svm_service" dotnet "/opt/svm/$svm_host/$svm_host.dll" --diagnostics || svm_result=3
    done
    if [[ "$(svm_value SVM_MODE)" == full ]]; then
      svm_vhost="$("${svm_compose[@]}" exec -T rabbitmq rabbitmqctl -q list_vhosts name --no-table-headers --timeout 10)"
      [[ "$svm_vhost" =~ ^svm-[0-9a-f-]{36}$ ]] || { echo 'Broker scope unavailable.' >&2; exit 3; }
      "${svm_compose[@]}" exec -T rabbitmq rabbitmqctl -q list_queues -p "$svm_vhost" name messages_ready messages_unacknowledged consumers --formatter json --timeout 10 || svm_result=3
    fi
    exit "$svm_result" ;;
  review-up)
    [[ $# == 2 && "$2" =~ ^[0-9]{4,5}$ && "$2" -ge 1024 && "$2" -le 65535 && -f "$svm_root/recovery/locked.json" ]] || { echo 'Usage: ./svm.sh review-up <unused-loopback-HTTPS-port>' >&2; exit 2; }
    svm_helper_command=(recovery-review --directory /deployment)
    svm_helper --mount "type=bind,src=$svm_root,dst=/deployment"
    # TLS is passed unchanged to the existing API certificate. Only this fixed relay
    # joins the loopback entry network; API/database keep their internal-only network.
    cat > "$svm_root/recovery/review-nginx.conf" <<'NGINX'
pid /tmp/nginx.pid;
error_log stderr warn;
events { worker_connections 128; }
stream {
  server { listen 10443; proxy_connect_timeout 5s; proxy_timeout 30s; proxy_pass api-a:7443; }
}
NGINX
    SVM_REVIEW_PORT="$2" "${svm_compose[@]}" up -d --pull never --wait --wait-timeout 120 postgres api-a review-entry
    echo 'Review only: loopback HTTPS, isolated network, no workers, no enrollment/upload/new task/start permit. Review original receipts and pause all unfinished deployments.' ;;
  reopen)
    [[ $# == 2 && -f "$2" && -f "$svm_root/recovery/locked.json" ]] || { echo 'Usage: ./svm.sh reopen /private/review-evidence.json' >&2; exit 2; }
    svm_safe_path "$2"
    if [[ "$(svm_value SVM_MODE)" == full ]]; then "${svm_compose[@]}" stop -t 30 review-entry gateway api-a api-b; else "${svm_compose[@]}" stop -t 30 review-entry api-a; fi
    "${svm_compose[@]}" run --rm --no-deps api-a /opt/svm/Svm.HttpApi/Svm.HttpApi.dll --operations-report > "$svm_root/recovery/pre-open-report.json"
    "${svm_compose[@]}" run --rm --no-deps api-a /opt/svm/Svm.HttpApi/Svm.HttpApi.dll --recovery-packages > "$svm_root/recovery/packages-report.json"
    if [[ "$2" != "$svm_root/recovery/review-input.json" ]]; then cp -- "$2" "$svm_root/recovery/review-input.json"; fi
    chmod 600 "$svm_root/recovery/review-input.json"
    svm_helper_command=(recovery-open --directory /deployment)
    svm_helper --mount "type=bind,src=$svm_root,dst=/deployment"
    "${svm_compose[@]}" down
    echo 'Review recorded; isolated containers/network stopped. Confirm the original deployment cannot resume, then explicitly run ./svm.sh up. Paused deployments stay paused.' ;;
  *) echo 'Commands: backup|verify-backup|restore|diagnostics|review-up|reopen' >&2; exit 2 ;;
esac
