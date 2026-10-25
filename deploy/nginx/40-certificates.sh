#!/bin/sh
# Пока сертификата нет, HTTPS-блоки отключаются и nginx отдаёт ACME-challenge по HTTP.
# После выпуска сертификата: docker compose restart nginx.
set -eu

CERT_NAME="${CERT_NAME:-aeza}"
CERT="/etc/letsencrypt/live/${CERT_NAME}/fullchain.pem"

rm -f /etc/nginx/conf.d/default.conf
if [ -f "$CERT" ]; then
    echo "40-certificates: TLS certificate ${CERT_NAME} found, HTTPS enabled"
else
    echo "40-certificates: TLS certificate ${CERT_NAME} not found — HTTP only (ACME) until certbot issues it"
    rm -f /etc/nginx/conf.d/*-https.conf
fi
