#!/usr/bin/env bash
set -euo pipefail
svm_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
[[ -f "$svm_directory/.svm-demo" ]] || { echo "Missing explicit deployment identity." >&2; exit 2; }
svm_project="$(cat "$svm_directory/.svm-demo")"
[[ "$svm_project" =~ ^svm-demo-[a-f0-9]{12}$ ]] || { echo "Invalid deployment identity." >&2; exit 2; }
svm_env="$svm_directory/deployment.env"
if [[ "${1:-}" == --offline ]]; then svm_env="$svm_directory/deployment.offline.env"; shift; fi
svm_command="${1:-}"
svm_value() { awk -F= -v key="$1" '$1 == key {print substr($0,index($0,"=")+1)}' "$svm_directory/deployment.env"; }
svm_validate_images() {
  [[ -f "$svm_directory/images.lock.tsv" && -f "$svm_directory/deployment.offline.env" ]] || { echo 'Missing offline image lock.' >&2; return 2; }
  while IFS=$'\t' read -r svm_key svm_original svm_tag svm_id svm_platform; do
    [[ "$svm_tag" == "svm-offline/$svm_project:"* && "$svm_id" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Invalid offline image lock.' >&2; return 2; }
    [[ "$(docker image inspect --format '{{.Id}} {{.Os}}/{{.Architecture}}' "$svm_tag")" == "$svm_id $svm_platform" ]] || { echo "Offline image mismatch: $svm_key" >&2; return 2; }
    [[ "$(awk -F= -v key="$svm_key" '$1 == key {print substr($0,index($0,"=")+1)}' "$svm_directory/deployment.offline.env")" == "$svm_tag" ]] || { echo 'Offline environment differs from its image lock.' >&2; return 2; }
  done < "$svm_directory/images.lock.tsv"
}
if [[ "$svm_command" == import ]]; then
  cd "$svm_directory"
  if command -v sha256sum >/dev/null; then sha256sum --check images.tar.sha256; else shasum -a 256 --check images.tar.sha256; fi
  docker load -i images.tar
  svm_validate_images
  exit 0
fi
[[ -f "$svm_env" ]] || { echo 'Missing deployment environment.' >&2; exit 2; }
if [[ "$svm_env" == *deployment.offline.env ]]; then svm_validate_images; fi
svm_compose=(docker compose --env-file "$svm_env" --project-name "$svm_project" -f "$svm_directory/compose.yaml")
if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then
  svm_compose+=(--profile full)
else
  svm_compose+=(-f "$svm_directory/compose.preview.yaml")
fi
case "$svm_command" in
  build) "${svm_compose[@]}" build api-a ;;
  pull)
    [[ "$svm_env" != *deployment.offline.env ]] || { echo "Offline mode never pulls images." >&2; exit 2; }
    if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then "${svm_compose[@]}" pull postgres rabbitmq gateway; else "${svm_compose[@]}" pull postgres; fi ;;
  dependencies)
    if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then "${svm_compose[@]}" up -d --pull never --wait --wait-timeout 120 postgres rabbitmq; else "${svm_compose[@]}" up -d --pull never --wait --wait-timeout 120 postgres; fi ;;
  preflight)
    "${svm_compose[@]}" --profile tools run --rm --no-deps migration /opt/svm/Svm.Migration/Svm.Migration.dll status --config /svm/init/migration.json
    if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then
      svm_types="$("${svm_compose[@]}" exec -T rabbitmq rabbitmqctl -q list_vhosts default_queue_type --no-table-headers)"
      [[ "$svm_types" == quorum ]] || { echo 'Demo vhost must use quorum as its default queue type.' >&2; exit 2; }
    fi ;;
  status|script|apply|seed)
    svm_config=migration.json; [[ "$svm_command" != seed ]] || svm_config=seed.json
    "${svm_compose[@]}" --profile tools run --rm --no-deps migration /opt/svm/Svm.Migration/Svm.Migration.dll "$svm_command" --config "/svm/init/$svm_config" ;;
  validate)
    "${svm_compose[@]}" run --rm --no-deps api-a /opt/svm/Svm.HttpApi/Svm.HttpApi.dll --validate-config
    if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then "${svm_compose[@]}" run --rm --no-deps worker-a /opt/svm/Svm.Worker/Svm.Worker.dll --validate-config; fi ;;
  up) printf 'Starting deployment %s, Release %s\n' "$svm_project" "$(svm_value SVM_CODE_COMMIT)"; "${svm_compose[@]}" up -d --pull never ;;
  stop) "${svm_compose[@]}" stop ;;
  down) "${svm_compose[@]}" down ;;
  ps) "${svm_compose[@]}" ps ;;
  logs) "${svm_compose[@]}" logs --tail 100 ;;
  export)
    umask 077
    svm_platform="$(svm_value SVM_PLATFORM)"
    svm_keys=(SVM_RUNTIME_IMAGE SVM_POSTGRES_IMAGE)
    if grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then svm_keys+=(SVM_RABBITMQ_IMAGE SVM_NGINX_IMAGE); fi
    svm_images=()
    : > "$svm_directory/images.lock.tsv"
    awk '!/^SVM_(RUNTIME|POSTGRES|RABBITMQ|NGINX)_IMAGE=/' "$svm_directory/deployment.env" > "$svm_directory/deployment.offline.env"
    if ! grep -q '^SVM_MODE=full$' "$svm_directory/deployment.env"; then
      for svm_key in SVM_RABBITMQ_IMAGE SVM_NGINX_IMAGE; do printf '%s=%s\n' "$svm_key" "$(svm_value "$svm_key")" >> "$svm_directory/deployment.offline.env"; done
    fi
    for svm_key in "${svm_keys[@]}"; do
      svm_image="$(svm_value "$svm_key")"
      if ! docker image inspect "$svm_image" >/dev/null 2>&1; then
        [[ "$svm_key" != SVM_RUNTIME_IMAGE ]] || { echo 'Build the reviewed runtime image before export.' >&2; exit 2; }
        docker pull --platform "$svm_platform" "$svm_image"
      fi
      svm_actual="$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$svm_image")"
      [[ "$svm_actual" == "$svm_platform" ]] || { echo "Image platform mismatch: $svm_key" >&2; exit 2; }
      svm_id="$(docker image inspect --format '{{.Id}}' "$svm_image")"
      svm_tag="svm-offline/$svm_project:$(printf '%s' "$svm_key" | tr '[:upper:]' '[:lower:]')"
      docker tag "$svm_image" "$svm_tag"
      svm_images+=("$svm_tag")
      printf '%s\t%s\t%s\t%s\t%s\n' "$svm_key" "$svm_image" "$svm_tag" "$svm_id" "$svm_platform" >> "$svm_directory/images.lock.tsv"
      printf '%s=%s\n' "$svm_key" "$svm_tag" >> "$svm_directory/deployment.offline.env"
    done
    docker save -o "$svm_directory/images.tar" "${svm_images[@]}"
    (cd "$svm_directory"; if command -v sha256sum >/dev/null; then sha256sum images.tar; else shasum -a 256 images.tar; fi) > "$svm_directory/images.tar.sha256"
    svm_validate_images ;;
  destroy)
    [[ "${2:-}" == --delete-demo-data ]] || { echo "destroy requires --delete-demo-data; containers will be removed, bind-mounted data retained." >&2; exit 2; }
    "${svm_compose[@]}" --profile tools down --volumes --remove-orphans
    echo "Containers removed. Private files and bind-mounted data remain for explicit owner cleanup." ;;
  *) echo "Usage: ./svm.sh [--offline] build|pull|dependencies|preflight|status|script|apply|seed|validate|up|stop|down|ps|logs|export|import|destroy --delete-demo-data" >&2; exit 2 ;;
esac
