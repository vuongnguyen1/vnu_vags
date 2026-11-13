# SỔ TAY VẬN HÀNH & XỬ LÝ SỰ CỐ HỆ THỐNG VAGS (OPERATIONS RUNBOOK)
## VNU Academic Gateway (VAGS) - SysAdmin & SRE Runbook

> **Mục tiêu:** Cung cấp quy trình thao tác nhanh, chuẩn xác cho đội ngũ vận hành hệ thống và các Agent AI khi xử lý sự cố.  
> **Hạ tầng:** Cụm máy chủ chuyên dụng `112.137.142.30` (Ubuntu 24.04 LTS).

---

## 1. THÔNG TIN TRUY CẬP VÀ MÔI TRƯỜNG LÀM VIỆC

* **Địa chỉ IP máy chủ:** `112.137.142.30`
* **Tài khoản SSH:** `vags` (đã cấu hình SSH Key xác thực không cần mật khẩu)
* **Quyền hạn quản trị:** Sudo toàn quyền không cần nhập pass (`sudo ...`)
* **Thư mục ứng dụng:** `/mnt/data/websites/vags.vnu.edu.vn`
* **Công cụ quản trị chính:** `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh`

---

## 2. BẢNG TRA CỨU LỆNH NHANH (QUICK COMMAND CHEATSHEET)

| Tác vụ | Lệnh thực hiện |
| :--- | :--- |
| **Kiểm tra trạng thái toàn diện** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh status` |
| **Đổi tên miền chính** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh set <domain>` |
| **Thêm tên miền phụ (Alias)** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh add-alias <alias>` |
| **ĐÓNG CỔNG (Bật Bảo trì)** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh close` |
| **MỞ LẠI CỔNG (Tắt Bảo trì)** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh open` |
| **Đóng gói lại Extension** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh package-ext` |
| **Khởi động lại toàn bộ stack** | `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh restart` |
| **Xem log trực tiếp Backend** | `cd /mnt/data/websites/vags.vnu.edu.vn && docker compose logs -f vags-backend` |
| **Xem log trực tiếp HAProxy** | `cd /mnt/data/websites/vags.vnu.edu.vn && docker compose logs -f vags-proxy` |
| **Build lại Backend từ C#** | `cd /mnt/data/websites/vags.vnu.edu.vn && docker compose build vags-backend && docker compose up -d vags-backend` |

---

## 3. QUY TRÌNH XỬ LÝ SỰ CỐ KHẨN CẤP (EMERGENCY PROCEDURES)

### 3.1. Sự Cố 1: Cần Bảo Trì Khẩn Cấp Hoặc Nâng Cấp Server
**Mục tiêu:** Không để Cán bộ trường bị lỗi kết nối hoặc gián đoạn mạng khi máy chủ đang bảo trì.
1. Chạy lệnh đóng cổng:
   ```bash
   /mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh close
   ```
2. **Cơ chế tự vệ kích hoạt ngay lập tức:**
   * File `/vags.pac` trả về `return 'DIRECT';` cho toàn bộ Extension $\rightarrow$ Toàn bộ máy tính cán bộ duyệt web trực tiếp qua mạng internet của họ bình thường.
   * Cổng web trả về thông báo bảo trì chuyên nghiệp kèm mã 503.
   * Giao diện `/admin` vẫn mở để Quản trị viên theo dõi.
3. Sau khi bảo trì xong, mở lại hệ thống:
   ```bash
   /mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh open
   ```

### 3.2. Sự Cố 2: Phát Hiện Tài Khoản Bị Lộ Hoặc Tấn Công Lạm Dụng Băng Thông
1. Mở trang quản trị: `https://vags.vnu.edu.vn/admin` (hoặc `https://112.137.142.30/admin`).
2. Vào mục **Quản Lý Phiên & Tài Khoản**:
   * Nhấp **Khoá Tài Khoản** đối với username vi phạm.
   * Nhấp **Ép Đăng Xuất (Force Logout)**.
3. Hoặc thực hiện qua API dòng lệnh:
   ```bash
   curl -k -X POST https://127.0.0.1:443/api/v1/admin/users/lock \
        -H "Content-Type: application/json" \
        -d '{"user":"taikhoan_vipham"}'
   ```
   Lệnh này sẽ phát sóng SSE tức thì ngắt kết nối trên Extension của tài khoản đó.

### 3.3. Sự Cố 3: Tiến Trình Backend Bị Treo Hoặc Không Phản Hồi
1. Kiểm tra logs:
   ```bash
   docker logs --tail 50 vags_backend
   ```
2. Khởi động lại container backend độc lập (không làm rớt kết nối HAProxy):
   ```bash
   cd /mnt/data/websites/vags.vnu.edu.vn
   docker compose restart vags-backend
   ```
3. Kiểm tra kiểm tra sức khỏe cục bộ:
   ```bash
   curl -k https://127.0.0.1:443/api/v1/health
   ```

---

## 4. BẢO TRÌ CƠ SỞ DỮ LIỆU SQLITE & SAO LƯU

File cơ sở dữ liệu nằm tại: `/mnt/data/websites/vags.vnu.edu.vn/data/vags_monitor.db`.
Hệ thống sử dụng chế độ **SQLite WAL (Write-Ahead Logging)** cho tốc độ ghi cao và không bao giờ lock bảng.

### 4.1. Lệnh Sao Lưu Nhanh SQLite (Hot Backup)
```bash
sqlite3 /mnt/data/websites/vags.vnu.edu.vn/data/vags_monitor.db ".backup /mnt/data/websites/vags.vnu.edu.vn/data/vags_monitor_backup_$(date +%F).db"
```

### 4.2. Kiểm Tra Kích Thước và Tối Ưu Bảng
```bash
ls -lh /mnt/data/websites/vags.vnu.edu.vn/data/vags_monitor.db*
# Dọn dẹp không gian trống nếu cần:
sqlite3 /mnt/data/websites/vags.vnu.edu.vn/data/vags_monitor.db "VACUUM;"
```

---

## 5. DANH MỤC API KIỂM THỬ SỨC KHỎE (HEALTH CHECKS)

* **Healthcheck:** `GET https://112.137.142.30/api/v1/health`
* **System Status:** `GET https://112.137.142.30/api/v1/status`
* **PAC Script:** `GET https://112.137.142.30/vags.pac`
* **IP Leak Check:** `GET https://112.137.142.30/api/v1/ip-check`
* **Admin Dashboard Analytics:** `GET https://112.137.142.30/api/v1/admin/dashboard`

