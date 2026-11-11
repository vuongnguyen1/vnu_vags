#!/usr/bin/env bash
# Kịch bản kiểm thử tự động 5 giai đoạn cho hệ thống VNU Academic Gateway (vags.vnu.edu.vn)
set -euo pipefail

GREEN='\033[0;32m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}=================================================================${NC}"
echo -e "${BLUE}   KIỂM THỬ HỆ THỐNG VNU ACADEMIC GATEWAY (vags.vnu.edu.vn:443)  ${NC}"
echo -e "${BLUE}=================================================================${NC}"

# GIAI ĐOẠN 1: HẠ TẦNG, DOCKER, NGINX & CHỨNG CHỈ SSL
echo -e "\n${BLUE}[GIAI ĐOẠN 1] Kiểm tra Hạ tầng, Container Docker, Nginx & SSL *.vnu.edu.vn...${NC}"
docker compose -f /mnt/data/websites/vags.vnu.edu.vn/docker-compose.yml ps
/www/server/nginx/sbin/nginx -t
HTTP_CODE_WEB=$(curl -sk -o /dev/null -w "%{http_code}" https://vags.vnu.edu.vn/)
echo -e " -> Trạng thái HTTPS Admin Dashboard (https://vags.vnu.edu.vn/): HTTP ${HTTP_CODE_WEB}"
if [[ "${HTTP_CODE_WEB}" == "200" ]]; then
    echo -e " -> ${GREEN}[PASS] Giai đoạn 1: Hạ tầng Web & SSL hoạt động hoàn hảo!${NC}"
else
    echo -e " -> ${RED}[FAIL] Giai đoạn 1: HTTP code = ${HTTP_CODE_WEB}${NC}"
    exit 1
fi

# GIAI ĐOẠN 2: REST API, LOGIC PHÂN LOẠI PAC & LƯU TRỮ BỀN VỮNG
echo -e "\n${BLUE}[GIAI ĐOẠN 2] Kiểm tra REST API, Thứ tự ưu tiên Whitelist/Blacklist & Lưu trữ rules.json...${NC}"
VER_JSON=$(curl -sk https://vags.vnu.edu.vn/api/v1/rules/version)
echo " -> GET /api/v1/rules/version: ${VER_JSON}"

EVAL_LIC=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/rules/evaluate -H "Content-Type: application/json" -d '{"host":"lic.vnu.edu.vn"}')
EVAL_PORTAL=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/rules/evaluate -H "Content-Type: application/json" -d '{"host":"portal.vnu.edu.vn"}')
EVAL_SD=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/rules/evaluate -H "Content-Type: application/json" -d '{"host":"www.sciencedirect.com"}')
EVAL_IEEE=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/rules/evaluate -H "Content-Type: application/json" -d '{"host":"ieeexplore.ieee.org"}')
EVAL_YT=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/rules/evaluate -H "Content-Type: application/json" -d '{"host":"www.youtube.com"}')

echo " -> Evaluate lic.vnu.edu.vn        : ${EVAL_LIC}"
echo " -> Evaluate portal.vnu.edu.vn     : ${EVAL_PORTAL}"
echo " -> Evaluate www.sciencedirect.com : ${EVAL_SD}"
echo " -> Evaluate ieeexplore.ieee.org   : ${EVAL_IEEE} (Kỳ vọng: DIRECT)"
echo " -> Evaluate www.youtube.com       : ${EVAL_YT}"

if echo "${EVAL_LIC}" | grep -q '"action": *"PROXY"' && \
   echo "${EVAL_PORTAL}" | grep -q '"action": *"DIRECT"' && \
   echo "${EVAL_SD}" | grep -q '"action": *"PROXY"' && \
   echo "${EVAL_IEEE}" | grep -q '"action": *"DIRECT"' && \
   echo "${EVAL_YT}" | grep -q '"action": *"DIRECT"'; then
    echo -e " -> ${GREEN}[PASS] Giai đoạn 2: API và thứ tự ưu tiên phân loại tên miền chính xác 100%!${NC}"
else
    echo -e " -> ${RED}[FAIL] Giai đoạn 2: Phân loại tên miền không khớp kỳ vọng!${NC}"
    exit 1
fi

# GIAI ĐOẠN 3: ĐỒNG BỘ REAL-TIME QUA SERVER-SENT EVENTS (SSE)
echo -e "\n${BLUE}[GIAI ĐOẠN 3] Kiểm thử Đồng bộ Real-time qua Server-Sent Events (SSE)...${NC}"
SSE_TMP=$(mktemp)
curl -skN https://vags.vnu.edu.vn/api/v1/rules/stream > "${SSE_TMP}" 2>/dev/null &
SSE_PID=$!
sleep 1.5

# Kiểm tra đã nhận gói CONNECTED chưa
if grep -q '"type": *"CONNECTED"' "${SSE_TMP}"; then
    echo -e " -> Đã nhận sự kiện khởi tạo SSE: $(grep 'CONNECTED' "${SSE_TMP}" | head -n 1)"
else
    kill "${SSE_PID}" 2>/dev/null || true
    rm -f "${SSE_TMP}"
    echo -e " -> ${RED}[FAIL] Không nhận được gói CONNECTED từ SSE Stream!${NC}"
    exit 1
fi

# Bắn cập nhật từ Admin API và kiểm tra SSE nhận RULES_UPDATED tức thời
POST_RES=$(curl -sk -X POST https://vags.vnu.edu.vn/api/v1/admin/rules \
    -H "Content-Type: application/json" \
    -d '{
      "globalAcademicWhitelist": ["*.sciencedirect.com", "sciencedirect.com", "*.springer.com", "springer.com", "*.nature.com", "nature.com", "*.acm.org"],
      "internalWhitelist": ["lic.vnu.edu.vn", "*.lic.vnu.edu.vn"],
      "blacklist": ["*.youtube.com", "youtube.com", "*.facebook.com", "facebook.com", "*.tiktok.com", "tiktok.com", "*.ieee.org", "ieee.org", "*.ieeexplore.ieee.org", "ieeexplore.ieee.org"],
      "authMode": "demo"
    }')
echo " -> POST /api/v1/admin/rules response: ${POST_RES}"
sleep 1
kill "${SSE_PID}" 2>/dev/null || true

if grep -q "event: RULES_UPDATED" "${SSE_TMP}" && grep -q "*.acm.org" "${SSE_TMP}"; then
    echo -e " -> ${GREEN}[PASS] Giai đoạn 3: SSE Real-time Stream nhận tức thời sự kiện RULES_UPDATED!${NC}"
else
    cat "${SSE_TMP}"
    rm -f "${SSE_TMP}"
    echo -e " -> ${RED}[FAIL] Giai đoạn 3: Không nhận được RULES_UPDATED qua SSE!${NC}"
    exit 1
fi
rm -f "${SSE_TMP}"

# GIAI ĐOẠN 4: KIỂM THỬ HTTPS FORWARD PROXY (HTTP CONNECT TRÊN CỔNG 443) & CHẶN OPEN PROXY
echo -e "\n${BLUE}[GIAI ĐOẠN 4] Kiểm thử HTTPS CONNECT Proxy trên cổng 443 (Whitelist vs Blacklist)...${NC}"
# 4.1. Thử truy cập trang trong Whitelist (lic.vnu.edu.vn) qua Proxy https://vags.vnu.edu.vn:443
ALLOW_HEADERS=$(curl -I -s --connect-timeout 8 --proxy-insecure -x https://vags.vnu.edu.vn:443 -U phongnd:pqtkt -k https://lic.vnu.edu.vn | tr -d '\r' | head -n 5 || true)
echo " -> Kết quả CONNECT tới lic.vnu.edu.vn (Whitelist):"
echo "${ALLOW_HEADERS}" | sed 's/^/    /'

# 4.2. Thử truy cập trang trong Blacklist (www.youtube.com) qua Proxy https://vags.vnu.edu.vn:443 -> Phải trả về 403 Forbidden
BLOCK_HEADERS=$(curl -I -s --connect-timeout 8 --proxy-insecure -x https://vags.vnu.edu.vn:443 -U phongnd:pqtkt -k https://www.youtube.com | tr -d '\r' | head -n 5 || true)
echo " -> Kết quả CONNECT tới www.youtube.com (Blacklist - Kỳ vọng 403 Forbidden):"
echo "${BLOCK_HEADERS}" | sed 's/^/    /'

# 4.3. Kiểm tra các website khác trên cùng máy chủ AAPanel vẫn hoạt động bình thường
OTHER_SITE_CODE=$(curl -sk -o /dev/null -w "%{http_code}" https://album.vnu.edu.vn/)
echo " -> Kiểm tra website khác trên server (https://album.vnu.edu.vn/): HTTP ${OTHER_SITE_CODE}"

if echo "${ALLOW_HEADERS}" | grep -q "200 Connection Established" && \
   echo "${BLOCK_HEADERS}" | grep -q "403 Forbidden" && \
   [[ "${OTHER_SITE_CODE}" == "200" || "${OTHER_SITE_CODE}" == "302" || "${OTHER_SITE_CODE}" == "307" ]]; then
    echo -e " -> ${GREEN}[PASS] Giai đoạn 4: HTTPS CONNECT Proxy trên cổng 443 hoạt động chuẩn xác & an toàn!${NC}"
else
    echo -e " -> ${RED}[FAIL] Giai đoạn 4: Kiểm tra lại phản hồi Proxy!${NC}"
    exit 1
fi

# GIAI ĐOẠN 5: KIỂM TRA BỘ CÀI EXTENSION & TRẠNG THÁI TỔNG THỂ
echo -e "\n${BLUE}[GIAI ĐOẠN 5] Kiểm tra gói tải Extension (/vags-extension.zip) & API Status...${NC}"
EXT_ZIP_CODE=$(curl -sk -o /dev/null -w "%{http_code}" https://vags.vnu.edu.vn/vags-extension.zip)
STATUS_JSON=$(curl -sk https://vags.vnu.edu.vn/api/v1/status)
echo " -> Tải https://vags.vnu.edu.vn/vags-extension.zip: HTTP ${EXT_ZIP_CODE}"
echo " -> GET /api/v1/status metrics:"
echo "${STATUS_JSON}" | grep -A 4 '"metrics":' | sed 's/^/    /'

if [[ "${EXT_ZIP_CODE}" == "200" ]]; then
    echo -e " -> ${GREEN}[PASS] Giai đoạn 5: Toàn bộ 5 giai đoạn kiểm thử đều ĐẠT (100% PASS)!${NC}"
else
    echo -e " -> ${RED}[FAIL] Giai đoạn 5: Không tải được vags-extension.zip!${NC}"
    exit 1
fi

