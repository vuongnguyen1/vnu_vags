#!/bin/bash
# ==============================================================================
# VAGS Gateway - Domain Switcher & Service Maintenance Controller
# VNU Academic Gateway (VAGS) - Trung tâm Quản trị Đại học số (ĐHQGHN)
# Node: 112.137.142.30 (Dedicated Ubuntu 24.04 LTS Server)
# ==============================================================================

set -e

BASE_DIR="/mnt/data/websites/vags.vnu.edu.vn"
ENV_FILE="$BASE_DIR/.env"
RULES_FILE="$BASE_DIR/data/rules.json"
FLAG_FILE="$BASE_DIR/data/maintenance.flag"

GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

show_usage() {
    cat << EOF
CÔNG CỤ QUẢN TRỊ TÊN MIỀN & BẢO TRÌ VAGS GATEWAY
Sử dụng: $0 <lệnh> [tham số]

Các lệnh khả dụng:
  set <domain>            Đổi tên miền chính (VD: $0 set vags.vnu.edu.vn)
  add-alias <domain>      Thêm tên miền phụ / bí danh (VD: $0 add-alias gateway.vnu.edu.vn)
  remove-alias <domain>   Xóa tên miền phụ khỏi danh sách
  close                   ĐÓNG CỔNG VAGS (Bật chế độ bảo trì, chuyển hướng DIRECT)
  open                    MỞ CỔNG VAGS (Tắt chế độ bảo trì, kích hoạt Proxy)
  status                  Kiểm tra trạng thái tên miền, bảo trì và sức khỏe cụm dịch vụ
  restart                 Khởi động lại toàn bộ stack Docker

Ví dụ:
  $0 set vags.vnu.edu.vn
  $0 add-alias gate.vnu.edu.vn
  $0 close
  $0 open
  $0 status
EOF
}

ensure_env() {
    if [ ! -f "$ENV_FILE" ]; then
        log_warn "File .env chưa tồn tại, tạo mới từ cấu hình mặc định..."
        cat << EOF > "$ENV_FILE"
PRIMARY_DOMAIN=vags.vnu.edu.vn
DOMAIN_ALIASES=
SERVER_IP=112.137.142.30
MAINTENANCE_MODE=false
KEYCLOAK_AUTHORITY=https://idp.vnu.edu.vn/auth/realms/vnu
KEYCLOAK_CLIENT_ID=vags-gateway
KEYCLOAK_CLIENT_SECRET=6m1VxNOdAaiN9Jsz1lrnQ006OvDFOnbk
EOF
    fi
}

get_env_val() {
    grep "^$1=" "$ENV_FILE" 2>/dev/null | cut -d '=' -f2- | tr -d '\r'
}

set_env_val() {
    local key="$1"
    local val="$2"
    ensure_env
    if grep -q "^$key=" "$ENV_FILE"; then
        sed -i "s|^$key=.*|$key=$val|" "$ENV_FILE"
    else
        echo "$key=$val" >> "$ENV_FILE"
    fi
}

reload_backend() {
    log_info "Áp dụng cấu hình và khởi động lại vags_backend..."
    cd "$BASE_DIR"
    docker compose up -d vags-backend
    sleep 2
    local health=$(curl -k -s https://127.0.0.1:443/api/v1/health || echo '{"status":"down"}')
    log_success "Trạng thái sau khi nạp: $health"
}

update_extensions_domain() {
    local new_domain="$1"
    local prev_domain=$(get_env_val "PRIMARY_DOMAIN")
    [ -z "$prev_domain" ] && prev_domain="vags.vnu.edu.vn"

    log_info "Cập nhật domain trong mã nguồn Extension ($prev_domain -> $new_domain)..."

    # 1. Cập nhật trong extension-chrome
    if [ -d "$BASE_DIR/extension-chrome" ]; then
        sed -i "s|const VAGS_BASE_URL = .*|const VAGS_BASE_URL = \"https://$new_domain\";|g" "$BASE_DIR/extension-chrome/background.js"
        sed -i "s|const VAGS_BASE_URL = .*|const VAGS_BASE_URL = \"https://$new_domain\";|g" "$BASE_DIR/extension-chrome/popup.js"
        sed -i "s|https://$prev_domain|https://$new_domain|g" "$BASE_DIR/extension-chrome/popup.html"
        sed -i "s|https://$prev_domain/\*|https://$new_domain/*|g" "$BASE_DIR/extension-chrome/manifest.json"
        
        # Đóng gói zip Chrome Extension
        mkdir -p "$BASE_DIR/public/downloads"
        (cd "$BASE_DIR/extension-chrome" && zip -q -r "$BASE_DIR/public/downloads/vnu-academic-gateway.zip" ./*)
        cp -f "$BASE_DIR/public/downloads/vnu-academic-gateway.zip" "$BASE_DIR/public/vags-extension-chrome.zip"
        cp -f "$BASE_DIR/public/downloads/vnu-academic-gateway.zip" "$BASE_DIR/public/vags-extension.zip"
        log_success "Đã đóng gói lại Chrome Extension (.zip) với tên miền $new_domain"

        # Đóng gói và ký số file .CRX3 cho Chrome Enterprise
        if command -v crx3 >/dev/null 2>&1 && [ -f "$BASE_DIR/keys/extension.pem" ]; then
            crx3 create -pk "$BASE_DIR/keys/extension.pem" -o "$BASE_DIR/public/downloads/vnu-academic-gateway.crx" "$BASE_DIR/extension-chrome"
            cp -f "$BASE_DIR/public/downloads/vnu-academic-gateway.crx" "$BASE_DIR/public/vags-extension.crx"
            log_success "Đã đóng gói và ký số CRX3 Chrome Extension với tên miền $new_domain"
        fi
    fi

    # 2. Cập nhật trong extension-firefox
    if [ -d "$BASE_DIR/extension-firefox" ]; then
        sed -i "s|const VAGS_BASE_URL = .*|const VAGS_BASE_URL = \"https://$new_domain\";|g" "$BASE_DIR/extension-firefox/background.js"
        sed -i "s|const VAGS_BASE_URL = .*|const VAGS_BASE_URL = \"https://$new_domain\";|g" "$BASE_DIR/extension-firefox/popup.js"
        sed -i "s|https://$prev_domain|https://$new_domain|g" "$BASE_DIR/extension-firefox/popup.html"
        sed -i "s|https://$prev_domain/\*|https://$new_domain/*|g" "$BASE_DIR/extension-firefox/manifest.json"
        
        # Đóng gói zip Firefox Extension
        (cd "$BASE_DIR/extension-firefox" && zip -q -r "$BASE_DIR/public/downloads/vnu-academic-gateway-firefox.zip" ./*)
        cp -f "$BASE_DIR/public/downloads/vnu-academic-gateway-firefox.zip" "$BASE_DIR/public/vags-extension-firefox.zip"
        cp -f "$BASE_DIR/public/downloads/vnu-academic-gateway-firefox.zip" "$BASE_DIR/public/vags-extension-ff.zip"
        log_success "Đã đóng gói lại Firefox Extension (.zip) với tên miền $new_domain"
    fi

    # 3. Cập nhật file cấu hình cập nhật XML cho Chrome Enterprise Policy
    for xml_path in "$BASE_DIR/public/vags-extension.xml" "$BASE_DIR/public/downloads/vags-extension.xml" "$BASE_DIR/public/updates/vags-extension.xml"; do
        if [ -f "$xml_path" ]; then
            sed -i "s|codebase='https://[^/']*/|codebase='https://$new_domain/|g" "$xml_path"
        fi
    done

    # 4. Cập nhật trong public/index.html và public/admin.html
    if [ -f "$BASE_DIR/public/index.html" ]; then
        sed -i "s|https://$prev_domain|https://$new_domain|g" "$BASE_DIR/public/index.html"
        sed -i "s|$prev_domain : 443|$new_domain : 443|g" "$BASE_DIR/public/index.html"
        sed -i "s|<b>https://$prev_domain</b>|<b>https://$new_domain</b>|g" "$BASE_DIR/public/index.html"
        log_success "Đã cập nhật domain trong public/index.html ($prev_domain -> $new_domain)"
    fi
    if [ -f "$BASE_DIR/public/admin.html" ]; then
        sed -i "s|https://$prev_domain|https://$new_domain|g" "$BASE_DIR/public/admin.html"
        sed -i "s|$prev_domain (Cổng Gateway)|$new_domain (Cổng Gateway)|g" "$BASE_DIR/public/admin.html"
        log_success "Đã cập nhật domain trong public/admin.html ($prev_domain -> $new_domain)"
    fi
}

cmd_set_primary() {
    local new_domain="$1"
    if [ -z "$new_domain" ]; then
        log_error "Vui lòng nhập tên miền mới! Ví dụ: $0 set vags.vnu.edu.vn"
        exit 1
    fi
    log_info "Đang đổi Tên miền chính sang: ${new_domain}..."
    update_extensions_domain "$new_domain"
    set_env_val "PRIMARY_DOMAIN" "$new_domain"
    
    # Cập nhật proxyHost trong data/rules.json nếu tồn tại
    if [ -f "$RULES_FILE" ]; then
        python3 -c "
import json
try:
    with open('$RULES_FILE', 'r') as f:
        data = json.load(f)
    data['proxyHost'] = '$new_domain'
    with open('$RULES_FILE', 'w') as f:
        json.dump(data, f, indent=2)
    print('[OK] Updated proxyHost in $RULES_FILE')
except Exception as e:
    print('[WARN] Could not update rules.json:', e)
"
    fi
    reload_backend
    log_success "Đã chuyển đổi thành công tên miền chính sang: $new_domain"
}

cmd_add_alias() {
    local alias="$1"
    if [ -z "$alias" ]; then
        log_error "Vui lòng nhập tên miền phụ! Ví dụ: $0 add-alias gateway.vnu.edu.vn"
        exit 1
    fi
    local current_aliases=$(get_env_val "DOMAIN_ALIASES")
    if [[ ",$current_aliases," == *",$alias,"* ]]; then
        log_warn "Tên miền phụ $alias đã tồn tại trong danh sách."
        return
    fi
    local updated_aliases
    if [ -z "$current_aliases" ]; then
        updated_aliases="$alias"
    else
        updated_aliases="$current_aliases,$alias"
    fi
    set_env_val "DOMAIN_ALIASES" "$updated_aliases"
    reload_backend
    log_success "Đã thêm tên miền phụ: $alias (Tổng danh sách: $updated_aliases)"
}

cmd_remove_alias() {
    local alias="$1"
    if [ -z "$alias" ]; then
        log_error "Vui lòng nhập tên miền phụ cần xóa!"
        exit 1
    fi
    local current_aliases=$(get_env_val "DOMAIN_ALIASES")
    local updated_aliases=$(echo "$current_aliases" | tr ',' '\n' | grep -v "^$alias$" | paste -sd, -)
    set_env_val "DOMAIN_ALIASES" "$updated_aliases"
    reload_backend
    log_success "Đã xóa bí danh: $alias (Còn lại: $updated_aliases)"
}

cmd_close_vags() {
    log_warn "=== ĐANG ĐÓNG CỔNG VAGS (KÍCH HOẠT CHẾ ĐỘ BẢO TRÌ) ==="
    touch "$FLAG_FILE"
    set_env_val "MAINTENANCE_MODE" "true"
    reload_backend
    log_warn "Hệ thống Cổng VAGS đã chuyển sang trạng thái BẢO TRÌ (503 Service Unavailable)."
    log_warn "Trang chào sẽ thông báo bảo trì, các truy vấn học thuật sẽ bypass DIRECT để tránh gián đoạn internet của Cán bộ."
}

cmd_open_vags() {
    log_info "=== ĐANG MỞ LẠI CỔNG VAGS (TẮT BẢO TRÌ, SẴN SÀNG PHỤC VỤ) ==="
    rm -f "$FLAG_FILE"
    set_env_val "MAINTENANCE_MODE" "false"
    reload_backend
    log_success "Hệ thống Cổng VAGS đã MỞ trở lại và hoạt động bình thường!"
}

cmd_status() {
    ensure_env
    local primary=$(get_env_val "PRIMARY_DOMAIN")
    local aliases=$(get_env_val "DOMAIN_ALIASES")
    local server_ip=$(get_env_val "SERVER_IP")
    local maintenance=$(get_env_val "MAINTENANCE_MODE")
    
    echo -e "=========================================================="
    echo -e "         BÁO CÁO TRẠNG THÁI HỆ THỐNG VAGS GATEWAY        "
    echo -e "=========================================================="
    echo -e "  Máy chủ Egress/IP:      ${GREEN}${server_ip}${NC}"
    echo -e "  Tên miền chính:         ${GREEN}${primary}${NC}"
    echo -e "  Tên miền phụ (Aliases): ${BLUE}${aliases:-<không có>}${NC}"
    if [ -f "$FLAG_FILE" ] || [ "$maintenance" = "true" ]; then
        echo -e "  Trạng thái Cổng VAGS:   ${RED}ĐANG ĐÓNG (BẢO TRÌ / MAINTENANCE)${NC}"
    else
        echo -e "  Trạng thái Cổng VAGS:   ${GREEN}ĐANG MỞ (HOẠT ĐỘNG BÌNH THƯỜNG)${NC}"
    fi
    echo -e "----------------------------------------------------------"
    echo -e "  Tiến trình Docker:"
    cd "$BASE_DIR" && docker compose ps
    echo -e "----------------------------------------------------------"
    echo -e "  Cổng mạng đang mở (HAProxy & Backend):"
    ss -tulpn | grep -E '80|443|8443|5030|5031' || true
    echo -e "----------------------------------------------------------"
    echo -e "  Kiểm tra phản hồi cục bộ:"
    local health=$(curl -k -s -m 3 https://127.0.0.1:443/api/v1/health || echo "Lỗi kết nối cổng 443")
    echo -e "  HTTPS 443 Health: $health"
    local pac_head=$(curl -k -s -m 3 https://127.0.0.1:443/vags.pac | head -n 3)
    echo -e "  PAC Script Sample: $pac_head"
    echo -e "=========================================================="
}

cmd_restart() {
    log_info "Khởi động lại toàn bộ stack VAGS..."
    cd "$BASE_DIR"
    docker compose down
    docker compose up -d
    sleep 3
    cmd_status
}

case "$1" in
    set)
        cmd_set_primary "$2"
        ;;
    add-alias)
        cmd_add_alias "$2"
        ;;
    remove-alias)
        cmd_remove_alias "$2"
        ;;
    close|maintenance-on|disable)
        cmd_close_vags
        ;;
    open|maintenance-off|enable)
        cmd_open_vags
        ;;
    status)
        cmd_status
        ;;
    restart)
        cmd_restart
        ;;
    package-ext)
        ensure_env
        update_extensions_domain "$(get_env_val PRIMARY_DOMAIN)"
        ;;
    *)
        show_usage
        exit 0
        ;;
esac

