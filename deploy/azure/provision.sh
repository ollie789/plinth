#!/usr/bin/env bash
# Provision Plinth on Azure Container Apps with an Azure Blob output store.
# Idempotent: every step is create-if-missing. Run after `az login`.
#
# usage: deploy/azure/provision.sh [image-tag]
# env:   PLINTH_ALLOWED_HOSTS  comma list of retailer CDN hosts (required)
#        PLINTH_SIGNING_KEY    HMAC key; generated and printed if unset
#        AZ_LOCATION           default australiaeast
#        AZ_RG                 default rg-plinth
#        AZ_STORAGE            default plinth<8 random chars> (must be globally unique, lowercase)
set -euo pipefail

TAG="${1:-0.1.0}"
IMAGE="ghcr.io/ollie789/plinth:${TAG}"
LOCATION="${AZ_LOCATION:-australiaeast}"
RG="${AZ_RG:-rg-plinth}"
ENV_NAME="plinth-env"
APP="plinth"
CONTAINER="tiles"
: "${PLINTH_ALLOWED_HOSTS:?set PLINTH_ALLOWED_HOSTS to the comma-separated retailer CDN hosts}"
SIGNING_KEY="${PLINTH_SIGNING_KEY:-$(openssl rand -hex 32)}"

STATE="$(dirname "$0")/.state"
mkdir -p "$STATE"
if [[ -z "${AZ_STORAGE:-}" ]]; then
  if [[ -f "$STATE/storage" ]]; then AZ_STORAGE="$(cat "$STATE/storage")"; else AZ_STORAGE="plinth$(openssl rand -hex 4)"; echo "$AZ_STORAGE" > "$STATE/storage"; fi
fi

echo "== resource group $RG in $LOCATION"
az group create -n "$RG" -l "$LOCATION" -o none

echo "== storage account $AZ_STORAGE + container $CONTAINER (public read on blobs, immutable cache headers set by Plinth)"
az storage account create -n "$AZ_STORAGE" -g "$RG" -l "$LOCATION" --sku Standard_LRS --kind StorageV2 --allow-blob-public-access true --min-tls-version TLS1_2 -o none
CONN="$(az storage account show-connection-string -n "$AZ_STORAGE" -g "$RG" -o tsv)"
az storage container create -n "$CONTAINER" --connection-string "$CONN" --public-access blob -o none

echo "== container apps environment $ENV_NAME"
az extension add --name containerapp --upgrade -o none 2>/dev/null || true
az provider register -n Microsoft.App --wait -o none
az provider register -n Microsoft.OperationalInsights --wait -o none
az containerapp env create -n "$ENV_NAME" -g "$RG" -l "$LOCATION" -o none 2>/dev/null || true

echo "== container app $APP from $IMAGE (0.5 vCPU / 1 GiB, min 1 max 3 replicas, spec 12.5)"
if az containerapp show -n "$APP" -g "$RG" -o none 2>/dev/null; then
  az containerapp update -n "$APP" -g "$RG" --image "$IMAGE" -o none
else
  az containerapp create -n "$APP" -g "$RG" --environment "$ENV_NAME" \
    --image "$IMAGE" --args api \
    --target-port 8080 --ingress external --transport auto \
    --cpu 0.5 --memory 1.0Gi --min-replicas 1 --max-replicas 3 \
    --scale-rule-name http --scale-rule-type http --scale-rule-http-concurrency 8 \
    --secrets "signing-key=$SIGNING_KEY" "storage-conn=$CONN" \
    --env-vars "PLINTH_ALLOWED_HOSTS=$PLINTH_ALLOWED_HOSTS" "PLINTH_STORE=azblob://$CONTAINER" \
               "PLINTH_SIGNING_KEY=secretref:signing-key" "PLINTH_AZURE_STORAGE_CONNECTION=secretref:storage-conn" \
               "PLINTH_ON_FAILURE=redirect" "PLINTH_MAX_INFLIGHT=4" \
    -o none
fi

FQDN="$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)"
echo
echo "Plinth is at https://$FQDN"
echo "  healthz: curl -s https://$FQDN/healthz"
echo "  version: curl -s https://$FQDN/version"
echo "Signing key (store it in the Mahir Vercel env as PLINTH_SIGNING_KEY; not printed again):"
echo "  $SIGNING_KEY"
echo
echo "Custom domain (img.lastlook.com.au): add a CNAME to $FQDN and an asuid TXT record, then"
echo "  az containerapp hostname add -n $APP -g $RG --hostname img.lastlook.com.au"
echo "  az containerapp hostname bind -n $APP -g $RG --hostname img.lastlook.com.au --environment $ENV_NAME --validation-method CNAME"
