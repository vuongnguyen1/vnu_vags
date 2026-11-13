# HƯỚNG DẪN KỸ THUẬT & VẬN HÀNH HỆ THỐNG VAGS GATEWAY
## (Dành cho Đội ngũ Kỹ thuật & Các Agent AI Tương lai)

> **Cập nhật ngày:** 12/11/2026  
> **Đơn vị chủ trì:** Trung tâm Quản trị Đại học số (ĐHQGHN)  
> **Máy chủ Triển khai:** Node Chuyên dụng `112.137.142.30` (Ubuntu 24.04 LTS Kernel 6.8.0, 1Gbps BBR)

---

## 1. TỔNG QUAN HẠ TẦNG & PHÂN CHIA Ổ CỨNG

Hệ thống VAGS (VNU Academic Gateway) được chuyển giao và vận hành độc lập trên máy chủ chuyên dụng tối ưu chuẩn quốc gia (không cài đặt aaPanel để loại bỏ 100% chi phí trung gian và xung đột port 443).

### 1.1. Cấu hình Phần cứng & Phân vùng Ổ đĩa
* **CPU:** Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz (Skylake-SP kiến trúc máy chủ doanh nghiệp).
* **RAM:** 16 GB DDR4 ECC (Mức tiêu thụ thông thường < 1 GB, chịu tải đỉnh > 15,000 kết nối đồng thời).
* **Phân vùng Hệ điều hành (SSD 60GB):**
  * Mount tại `/` (LVM `ubuntu-vg/ubuntu-lv` đã mở rộng lên **57 GB**, khả dụng > 48 GB).
  * Chứa nhân Linux, thư viện hệ thống Ubuntu 24.04 LTS.
* **Phân vùng Dữ liệu & Lưu trữ (SAS/SSD 150GB):**
  * Mount tại `/mnt/data` (Ext4, UUID cố định trong `/etc/fstab`, khả dụng > 140 GB).
  * Toàn bộ mã nguồn, chứng chỉ SSL, cơ sở dữ liệu SQLite, file log lưu lượng và Docker data-root đều nằm tại đây:
    * `/mnt/data/websites/vags.vnu.edu.vn/`: Thư mục mã nguồn và cấu hình chính.
    * `/mnt/data/docker/`: Toàn bộ images, layers, volumes của Docker (`"data-root": "/mnt/data/docker"`).

### 1.2. Kernel Hardening & Băng thông 1Gbps Wire-Speed
Các tham số tinh chỉnh nằm tại file `/etc/sysctl.d/99-vags-1gbps.conf`:
* `net.ipv4.tcp_congestion_control = bbr`: Thuật toán Google BBR tối ưu băng thông và giảm độ trễ tối đa.
* `net.core.somaxconn = 65535` & `net.ipv4.tcp_max_syn_backlog = 65535`: Hàng đợi đón nhận kết nối lớn, chống nghẽn khi mở kỳ duyệt bài/tra cứu cao điểm.
* `net.ipv4.tcp_rmem` & `net.ipv4.tcp_wmem`: Buffer mạng mở rộng lên 16MB cho đường truyền 1Gbps.
* `net.ipv4.ip_unprivileged_port_start = 0`: Cho phép container Docker bind cổng < 1024 an toàn.
* Giới hạn file descriptors (`/etc/security/limits.conf`): `nofile 1048576` (1 triệu descriptors).

---

## 2. KIẾN TRÚC LUỒNG DỮ LIỆU & PORT MAPPING

```
[ Trình duyệt / Extension Cán bộ VNU ]
                 │
                 │ (Cổng 80: HTTP Redirect -> 301 sang HTTPS)
                 │ (Cổng 443 / 8443: HTTPS CONNECT & Web Portal)
                 ▼
 ┌──────────────────────────────────────────────────────────────┐
 │                     HAProxy 2.8 Ingress                      │
 │      - Multi-threading: nbthread 4                           │
 │      - Max connections: 25,000                               │
 │      - SNI Inspection & PROXY Protocol v1                    │
 └──────────────────────────────┬───────────────────────────────┘
                                │ (127.0.0.1:5031 + send-proxy)
                                ▼
 ┌──────────────────────────────────────────────────────────────┐
 │             C# .NET 8 Unified Backend (Kestrel)              │
 │  - Kestrel Port 8080: HTTP Nội bộ (healthcheck)              │
 │  - Kestrel Port 8443: TLS Giải mã Wildcard (*.vnu.edu.vn)    │
 │  - Xử lý PROXY Protocol: Đọc đúng Real IP Cán bộ            │
 │  - HTTPS CONNECT Proxy: Đường hầm truy cập CSDL Khoa học    │
 │  - Trình phân phối PAC động (/vags.pac)                      │
 │  - Real-time SSE Stream (/api/v1/rules/stream)               │
 │  - SQLite WAL Database (/app/data/vags_monitor.db)           │
 └──────────────────────────────┬───────────────────────────────┘
                                │
                                ▼
 [ SSO Keycloak idp.vnu.edu.vn ] & [ CSDL Khoa học: ScienceDirect, Springer, Nature... ]
```

---

## 3. CÔNG CỤ QUẢN TRỊ TÊN MIỀN & BẢO TRÌ (`switch-domain.sh`)

Tại thư mục `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh` đã tích hợp script quản trị toàn diện:

### 3.1. Đổi Tên Miền Chính
Khi cần đổi tên miền máy chủ (ví dụ sang `gateway.vnu.edu.vn`):
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh set <domain>
# Ví dụ:
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh set gateway.vnu.edu.vn
```
* Tác động:
  * Tự động ghi đè biến `PRIMARY_DOMAIN` trong `.env`.
  * Cập nhật `proxyHost` trong `data/rules.json`.
  * Khởi động lại `vags_backend` trong ~2 giây.
  * Tự động phát sinh PAC Script mới trả về tên miền mới cho toàn bộ Extension.

### 3.2. Thêm hoặc Xóa Tên Miền Phụ (Aliases)
Nếu muốn hệ thống chạy đồng thời nhiều tên miền (ví dụ vừa `vags.vnu.edu.vn` vừa `gate.vnu.edu.vn`):
```bash
# Thêm bí danh:
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh add-alias gate.vnu.edu.vn

# Xóa bí danh:
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh remove-alias gate.vnu.edu.vn
```

### 3.3. Đóng Cổng VAGS (Kích hoạt Chế độ Bảo trì)
Khi cần nâng cấp, đổi máy chủ hoặc tạm dừng dịch vụ:
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh close
```
* Tác động tức thì:
  * Trang Web (`/`) trả về mã **503 Service Unavailable** cùng giao diện thông báo Bảo trì sang trọng chuẩn ĐHQGHN.
  * Các truy vấn CONNECT qua Proxy sẽ trả về thông báo bảo trì.
  * File PAC Script (`/vags.pac`) tự động chuyển thành `return 'DIRECT';` giúp toàn bộ máy tính của Cán bộ duyệt web bình thường trực tiếp, **tuyệt đối không bị lỗi rớt mạng hay tắc nghẽn internet**.
  * Trang Quản trị `/admin` và API `/api/v1/health` vẫn mở để Quản trị viên theo dõi và bật lại khi hoàn tất.

### 3.4. Mở Lại Cổng VAGS
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh open
```
* Phục hồi ngay lập tức toàn bộ phân quyền, luật Proxy và cổng chào.

### 3.5. Kiểm Tra Tình Trạng Toàn Diện
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh status
```

### 3.6. Cơ Chế Tự Động Đồng Bộ Tên Miền Vào Extension Trình Duyệt
Khi thực hiện lệnh `switch-domain.sh set <domain>`, script tự động:
1. Quét và thay thế hằng số `VAGS_BASE_URL` trong:
   * Chrome Extension: `extension-chrome/background.js`, `popup.js`, `content.js`, `manifest.json`.
   * Firefox Extension: `extension-firefox/background.js`, `popup.js`, `content.js`, `manifest.json`.
2. Tự động đóng gói lại file cài đặt `.zip` vào thư mục phục vụ tải về:
   * `public/downloads/vnu-academic-gateway.zip` & `public/vags-extension-chrome.zip`
   * `public/downloads/vnu-academic-gateway-firefox.zip` & `public/vags-extension-firefox.zip`
3. Cập nhật `codebase` trong các file chính sách cập nhật tự động `vags-extension.xml`.

*Lưu ý:* Nếu bạn chỉnh sửa thủ công code trong thư mục `extension-chrome/` hoặc `extension-firefox/`, bạn có thể chạy lệnh sau để đóng gói lại các file tải về mà không cần đổi domain:
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh package-ext
```

---

## 4. QUẢN TRỊ DOCKER & COMPOSE

Thư mục làm việc: `/mnt/data/websites/vags.vnu.edu.vn`

### 4.1. Lệnh thường dùng
```bash
cd /mnt/data/websites/vags.vnu.edu.vn

# Xem trạng thái containers
docker compose ps

# Xem nhật ký (Logs)
docker compose logs -f vags-backend
docker compose logs -f vags-proxy

# Build lại backend từ mã nguồn C# mới
docker compose build vags-backend && docker compose up -d vags-backend

# Khởi động lại toàn bộ
docker compose restart
```

### 4.2. Cấu trúc File & Thư mục Dự án
```
/mnt/data/websites/vags.vnu.edu.vn/
├── .env                          # Biến môi trường hệ thống (Domain, IP, Keycloak Secret)
├── docker-compose.yml            # Khởi tạo backend & proxy
├── DEVELOPER_AGENT_INSTRUCTIONS.md # Tài liệu kiến trúc này
├── certs/                        # Chứng chỉ SSL Wildcard *.vnu.edu.vn
│   ├── fullchain.pem
│   └── privkey.pem
├── backend/                      # Mã nguồn C# .NET 8 (Kestrel Core)
│   ├── Program.cs                # Unified Gateway, PAC, SSE, Proxy CONNECT
│   ├── MonitorDbService.cs       # Truy vấn & ghi log SQLite
│   ├── Dockerfile                # Multi-stage build .NET 8
│   └── VagsBackend.csproj
├── proxy/                        # Cấu hình HAProxy
│   └── haproxy.cfg               # Ingress port 80/443/8443
├── data/                         # Dữ liệu động bền vững
│   ├── vags_monitor.db           # SQLite Database ghi nhận lịch sử & lưu lượng
│   ├── rules.json                # Danh mục Whitelist, Blacklist, User Overrides
│   ├── maintenance.flag          # Cờ đánh dấu khi đang bảo trì
│   └── security.log              # Nhật ký sự kiện bảo mật
├── public/                       # Giao diện Web Client & Extension Downloads
│   ├── index.html                # Cổng chào đăng nhập Cán bộ
│   ├── admin.html                # Bảng điều khiển Quản trị viên
│   ├── downloads/                # File cài đặt .crx, .zip, .xpi
│   └── images/
└── scripts/                      # Bộ công cụ tự động hóa
    ├── switch-domain.sh          # Quản trị tên miền & bảo trì
    └── run-phased-tests.sh       # Kịch bản kiểm thử tự động
```

---

## 5. HƯỚNG DẪN DÀNH CHO CÁC AGENT AI KHI BẢO TRÌ & NÂNG CẤP

1. **Bảo toàn File Data & SQLite:**
   * Thư mục `/mnt/data/websites/vags.vnu.edu.vn/data` chứa dữ liệu người dùng thật (`vags_monitor.db` và `rules.json`). Tuyệt đối không xóa hoặc ghi đè rỗng thư mục này khi build lại container.
2. **Chứng chỉ SSL:**
   * Chứng chỉ nằm cố định tại `./certs/fullchain.pem` và `./certs/privkey.pem`. Đây là chứng chỉ Wildcard `*.vnu.edu.vn` có hiệu lực đến ngày 15/02/2027.
3. **Cơ chế Nhận diện Danh tính Cán bộ:**
   * Kestrel đọc IP thật thông qua PROXY Protocol v1 do HAProxy chuyển tiếp (`send-proxy`). Do đó mọi logic kiểm tra IP và phân giải danh tính Cán bộ (`monitorDb.ResolveUser`) phải lấy `clientIp` từ header kết nối đã giải mã.
4. **Không cài đặt Web Server trùng lặp:**
   * Port `80` và `443` hiện do HAProxy trực tiếp lắng nghe. Không cài thêm Nginx hay Apache ngoài máy chủ host để tránh tranh chấp socket.

---
*© 2026 Trung tâm Quản trị Đại học số - Đại học Quốc gia Hà Nội. Tài liệu kỹ thuật lưu hành nội bộ.*

