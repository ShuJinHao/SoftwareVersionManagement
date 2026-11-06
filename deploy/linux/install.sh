#!/usr/bin/env bash
# Public candidate bootstrap. Does not source private JSON or require source/SDK/Node.
set -euo pipefail
umask 077
svm_bundle="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
svm_action="${1:-}"
fail() { printf '%s\n' "$1" >&2; exit 2; }
safe_path() {
  [[ "$1" == /* && "$1" != / && "$1" != *$'\n'* && "$1" != *$'\r'* && "$1" != *','* && "$1" != *':'* && "$1" != *'\\'* ]] || fail 'Absolute ordinary path required.'
  local p="$1"
  while [[ "$p" != / ]]; do [[ ! -L "$p" ]] || fail 'Symbolic links are not installation paths.'; p="$(dirname -- "$p")"; done
}
verify_files() {
  [[ -f "$svm_bundle/SHA256SUMS" && -f "$svm_bundle/images.lock.tsv" ]] || fail 'Incomplete candidate.'
  # Strict names before coreutils reads the inventory. The outer tar hash must be
  # compared with the separately delivered trusted hash before extracting it.
  awk 'length($1)!=64 || $1 !~ /^[0-9a-f]+$/ || substr($0,65,2)!="  " {exit 1}
       {p=substr($0,67); if(p !~ /^[-A-Za-z0-9_.\/@+]+$/ || p ~ /(^|\/)\.\.?($|\/)/ || p ~ /^\// || p=="SHA256SUMS" || seen[p]++)exit 1}' "$svm_bundle/SHA256SUMS" || fail 'Invalid candidate checksum inventory.'
  [[ -z "$(find "$svm_bundle" -type l -print -quit)" ]] || fail 'Candidate contains a link.'
  [[ "$(find "$svm_bundle" -type f | wc -l)" -eq $(( $(wc -l < "$svm_bundle/SHA256SUMS") + 1 )) ]] || fail 'Unlisted candidate content.'
  (cd "$svm_bundle" && sha256sum --check --strict --quiet SHA256SUMS) || fail 'Candidate checksum mismatch.'
}
check_engine() {
  local architecture
  architecture="$(docker info --format '{{.OSType}}/{{.Architecture}}')"
  [[ "$architecture" == linux/x86_64 || "$architecture" == linux/amd64 ]] || fail 'This candidate requires a native Linux x64 Docker Engine.'
  local version major minor
  version="$(docker compose version --short)"; version="${version#v}"
  [[ "$version" =~ ^([0-9]+)\.([0-9]+)\. ]] || fail 'Docker Compose v2.24+ required.'
  major="${BASH_REMATCH[1]}"; minor="${BASH_REMATCH[2]}"
  (( major > 2 || major == 2 && minor >= 24 )) || fail 'Docker Compose v2.24+ required.'
}
inspect_images() {
  local role reference tag image platform count=0
  svm_base=''
  while IFS=$'\t' read -r role reference tag image platform; do
    [[ "$role" =~ ^(ASPNET|POSTGRES|RABBITMQ|NGINX)$ && "$tag" =~ ^svm-candidate/rc-[0-9]{8}-[a-f0-9]{7,40}:[a-z]+$ && "$image" =~ ^sha256:[a-f0-9]{64}$ && "$platform" == linux/amd64 ]] || fail 'Invalid candidate image lock.'
    [[ "$(docker image inspect --format '{{.Id}} {{.Os}}/{{.Architecture}}' "$tag")" == "$image linux/amd64" ]] || fail 'Candidate image ID or architecture differs.'
    [[ "$role" != ASPNET ]] || svm_base="$tag"
    count=$((count+1))
  done < "$svm_bundle/images.lock.tsv"
  [[ "$count" == 4 && -n "$svm_base" ]] || fail 'Incomplete image lock.'
}
technical() {
  docker run --rm --pull never --platform linux/amd64 --network none --read-only --tmpfs /tmp --security-opt no-new-privileges:true \
    --mount "type=bind,source=$svm_bundle,target=/bundle,readonly" "${svm_mounts[@]}" \
    --entrypoint dotnet "$svm_base" /bundle/release/Svm.Migration/Svm.Migration.dll "$@"
}
safe_path "$svm_bundle"
case "$svm_action" in verify|load|check|init|build) ;; *) fail 'Usage: install.sh verify|load|check --settings private.json|init --settings private.json|build --directory private-directory';; esac
verify_files
if [[ "$svm_action" == verify ]]; then echo 'Candidate file hashes verified; compare the external archive hash before trusting this bundle.'; exit 0; fi
check_engine
if [[ "$svm_action" == load ]]; then
  [[ "$#" == 1 ]] || fail 'load takes no additional arguments.'
  docker load -i "$svm_bundle/images.tar"
fi
inspect_images
svm_mounts=()
technical candidate-verify --bundle /bundle
if [[ "$svm_action" == load ]]; then exit 0; fi
if [[ "$svm_action" == check || "$svm_action" == init ]]; then
  [[ "$#" == 3 && "$2" == --settings ]] || fail 'Supply --settings private.json.'
  safe_path "$3"; [[ -f "$3" ]] || fail 'Settings must be a regular file.'
  [[ "$(stat -c '%a' "$3")" == 600 ]] || fail 'Settings require mode 0600.'
  svm_mounts=(--mount "type=bind,source=$3,target=/input/settings.json,readonly")
  svm_plan="$(technical acceptance-check --settings /input/settings.json)"
  svm_destination="$(printf '%s\n' "$svm_plan" | sed -n '1p')"
  svm_uid="$(printf '%s\n' "$svm_plan" | sed -n '2p')"; svm_gid="$(printf '%s\n' "$svm_plan" | sed -n '3p')"
  [[ "$svm_uid" =~ ^[0-9]+$ && "$svm_gid" =~ ^[0-9]+$ ]] || fail 'Invalid service identity.'
  safe_path "$svm_destination"
  [[ "$svm_destination" != "$svm_bundle" && "$svm_destination" != "$svm_bundle/"* && "$svm_bundle" != "$svm_destination/"* ]] || fail 'Private data must be separate from the public bundle.'
  [[ ! -e "$svm_destination" && -d "$(dirname -- "$svm_destination")" ]] || fail 'Use a new directory under an existing parent; existing data are never reset.'
  if [[ "$(id -u)" != 0 ]]; then
    [[ "$(id -u)" == "$svm_uid" && "$(id -g)" == "$svm_gid" ]] || fail 'Run as root or as the selected service UID/GID.'
  fi
  if [[ "$svm_action" == check ]]; then echo 'Explicit settings valid; no directories, database or services changed.'; exit 0; fi
  mkdir -m 700 -- "$svm_destination" # Atomic ownership of the new output; never mkdir -p over data.
  svm_mounts+=(--user "$(id -u):$(id -g)" --mount "type=bind,source=$svm_destination,target=/output")
  technical acceptance-init --settings /input/settings.json --bundle /bundle --output /output
  if [[ "$(id -u)" == 0 ]]; then chown -R "$svm_uid:$svm_gid" -- "$svm_destination"; fi
  echo 'Private initialization complete. No database migrated, administrator seeded or services started. Build explicitly next.'
  exit 0
fi
[[ "$#" == 3 && "$2" == --directory ]] || fail 'Supply --directory private-directory.'
svm_destination="$3"; safe_path "$svm_destination"
[[ -f "$svm_destination/.svm-deployment" && ! -e "$svm_destination/.initializing" && ! -e "$svm_destination/recovery/locked.json" ]] || fail 'A complete new acceptance deployment is required.'
svm_project="$(cat "$svm_destination/.svm-deployment")"
[[ "$svm_project" =~ ^svm-site-[a-f0-9]{12}$ ]] || fail 'Invalid acceptance deployment identity.'
svm_value() { awk -F= -v key="$1" '$1 == key {print substr($0,index($0,"=")+1)}' "$svm_destination/deployment.env"; }
svm_runtime="$(svm_value SVM_RUNTIME_IMAGE)"
[[ "$svm_runtime" == "svm/runtime:$svm_project" && "$(svm_value SVM_PLATFORM)" == linux/amd64 ]] || fail 'Invalid runtime identity.'
cmp "$svm_bundle/release/release.json" "$svm_destination/image/release/release.json" >/dev/null || fail 'Candidate differs from deployment.'
cmp "$svm_bundle/templates/Dockerfile" "$svm_destination/image/Dockerfile" >/dev/null || fail 'Runtime build recipe differs.'
(cd "$svm_destination/image" && awk 'substr($0,67,8)=="release/"' "$svm_bundle/SHA256SUMS" | sha256sum --check --strict --quiet) || fail 'Private copy of the release differs from candidate.'
[[ ! -e "$svm_destination/deployment.offline.env" && ! -e "$svm_destination/images.lock.tsv" ]] || fail 'Runtime was already assembled; existing image lock is not overwritten.'
docker build --pull=false --network none --platform linux/amd64 --tag "$svm_runtime" "$svm_destination/image"
svm_image="$(docker image inspect --format '{{.Id}}' "$svm_runtime")"
svm_tag="svm-offline/$svm_project:svm_runtime_image"
docker tag "$svm_runtime" "$svm_tag"
awk '!/^SVM_(RUNTIME|POSTGRES|RABBITMQ|NGINX)_IMAGE=/' "$svm_destination/deployment.env" > "$svm_destination/deployment.offline.env"
printf '%s\t%s\t%s\t%s\t%s\n' SVM_RUNTIME_IMAGE "$svm_runtime" "$svm_tag" "$svm_image" linux/amd64 > "$svm_destination/images.lock.tsv"
printf 'SVM_RUNTIME_IMAGE=%s\n' "$svm_tag" >> "$svm_destination/deployment.offline.env"
while IFS=$'\t' read -r svm_role svm_reference svm_source svm_id svm_platform; do
  [[ "$svm_role" != ASPNET ]] || continue
  svm_key="SVM_${svm_role}_IMAGE"; svm_tag="svm-offline/$svm_project:$(printf '%s' "$svm_key" | tr '[:upper:]' '[:lower:]')"
  docker tag "$svm_source" "$svm_tag"
  printf '%s\t%s\t%s\t%s\t%s\n' "$svm_key" "$svm_reference" "$svm_tag" "$svm_id" "$svm_platform" >> "$svm_destination/images.lock.tsv"
  printf '%s=%s\n' "$svm_key" "$svm_tag" >> "$svm_destination/deployment.offline.env"
done < "$svm_bundle/images.lock.tsv"
if [[ "$(id -u)" == 0 ]]; then chown "$(svm_value SVM_UID):$(svm_value SVM_GID)" "$svm_destination/deployment.offline.env" "$svm_destination/images.lock.tsv"; fi
echo 'Offline runtime assembled and locked. Use svm.sh --offline; review and explicitly apply migrations and seed.'
