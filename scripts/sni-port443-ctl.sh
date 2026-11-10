#!/usr/bin/env bash
# Script quản lý định tuyến TLS SNI trên cổng 443 cho vags.vnu.edu.vn
# Cho phép cổng 443 vừa chạy tất cả website AAPanel (Nginx), vừa xử lý HTTP CONNECT cho HTTPS vags.vnu.edu.vn:443
set -euo pipefail

ACTION="${1:-status}"
HAPROXY_PORT=8443
HAPROXY_UID=99
EXT_IFACE="ens160"

enable_sni() {
    # Đảm bảo HAProxy đang lắng nghe trên cổng 8443
    if ! ss -tln | grep -q ":${HAPROXY_PORT}\b"; then
        echo "[ERROR] vags_proxy (HAProxy) chưa lắng nghe trên cổng ${HAPROXY_PORT}!"
        exit 1
    fi

    # 1. Chuyển hướng lưu lượng cổng 443 từ card mạng ngoài (ens160) qua HAProxy SNI Router (8443)
    if ! iptables -t nat -C PREROUTING -i "${EXT_IFACE}" -p tcp --dport 443 -j REDIRECT --to-ports "${HAPROXY_PORT}" 2>/dev/null; then
        iptables -t nat -A PREROUTING -i "${EXT_IFACE}" -p tcp --dport 443 -j REDIRECT --to-ports "${HAPROXY_PORT}"
    fi

    # 2. Chuyển hướng lưu lượng kiểm thử nội bộ tới 127.0.0.1:443 / 112.137.133.14:443 (trừ tiến trình HAProxy UID 99 để tránh vòng lặp)
    if ! iptables -t nat -C OUTPUT -p tcp -d 127.0.0.1 --dport 443 -m owner ! --uid-owner "${HAPROXY_UID}" -j REDIRECT --to-ports "${HAPROXY_PORT}" 2>/dev/null; then
        iptables -t nat -A OUTPUT -p tcp -d 127.0.0.1 --dport 443 -m owner ! --uid-owner "${HAPROXY_UID}" -j REDIRECT --to-ports "${HAPROXY_PORT}"
    fi

    echo "[OK] Đã bật định tuyến SNI cổng 443 -> HAProxy (${HAPROXY_PORT}) -> C# Unified Gateway (5031) & Nginx (443)."
}

disable_sni() {
    iptables -t nat -D PREROUTING -i "${EXT_IFACE}" -p tcp --dport 443 -j REDIRECT --to-ports "${HAPROXY_PORT}" 2>/dev/null || true
    iptables -t nat -D OUTPUT -p tcp -d 127.0.0.1 --dport 443 -m owner ! --uid-owner "${HAPROXY_UID}" -j REDIRECT --to-ports "${HAPROXY_PORT}" 2>/dev/null || true
    echo "[OK] Đã tắt định tuyến SNI cổng 443 (Mọi kết nối 443 đi thẳng vào Nginx)."
}

status_sni() {
    echo "=== Trạng thái iptables NAT (Port 443 SNI Multiplexing) ==="
    iptables -t nat -L PREROUTING -n -v --line-numbers | grep -E "8443|443" || echo "PREROUTING: Không có rule redirect 8443"
    iptables -t nat -L OUTPUT -n -v --line-numbers | grep -E "8443|443" || echo "OUTPUT: Không có rule redirect 8443"
}

case "${ACTION}" in
    enable) enable_sni ;;
    disable) disable_sni ;;
    status) status_sni ;;
    *)
        echo "Usage: $0 {enable|disable|status}"
        exit 1
        ;;
esac

