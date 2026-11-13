# HƯỚNG DẪN MÃ NGUỒN, CẤU HÌNH & PHÂN PHỐI BROWSER EXTENSION
## VNU Academic Gateway (VAGS) - Browser Extension Documentation

> **Đối tượng:** Đội ngũ phát triển Frontend/Extension, Quản trị viên Google Workspace ĐHQGHN, và các AI Agent  
> **Phiên bản:** Manifest V3 (Google Chrome / Chromium) & WebExtensions (Mozilla Firefox)

---

## 1. TỔNG QUAN TIỆN ÍCH TRÌNH DUYỆT (BROWSER EXTENSION)

Tiện ích mở rộng VAGS đóng vai trò là "cầu nối thông minh" trên máy tính của Cán bộ, Giảng viên và Nhà nghiên cứu ĐHQGHN. Nó thực hiện:
1. **Tự động cấu hình Proxy PAC (Proxy Auto-Config):** Chỉ chuyển tiếp các truy vấn đến CSDL Khoa học bản quyền (ScienceDirect, IEEE, Springer, Nature, v.v.) qua Cổng VAGS `https://vags.vnu.edu.vn:443`.
2. **Bypass DIRECT an toàn:** Toàn bộ lưu lượng Google Workspace trường (`*.google.com`, Gmail, Drive) và mạng internet cá nhân (YouTube, Facebook, báo chí) đi thẳng không qua proxy, bảo vệ sự riêng tư và chống nghẽn đường truyền.
3. **Đồng bộ trạng thái thời gian thực qua Server-Sent Events (SSE):** Tự động nhận luật mới, thay đổi tên miền hoặc lệnh đóng/mở cổng bảo trì từ máy chủ VAGS mà không cần người dùng thao tác gì.
4. **Cầu nối SSO tự động (Single Sign-On Bridge):** Khi người dùng đăng nhập tài khoản ĐHQGHN trên cổng web, Extension tự động nhận diện và kích hoạt phiên ủy quyền.

---

## 2. CẤU TRÚC THƯ MỤC MÃ NGUỒN EXTENSION

Mã nguồn được lưu trữ tại 2 thư mục chính:

```
/mnt/data/websites/vags.vnu.edu.vn/
├── extension-chrome/             # Dành cho Chrome, Edge, Cốc Cốc, Brave (Manifest V3)
│   ├── manifest.json             # Khai báo quyền (proxy, storage, declarativeNetRequest)
│   ├── background.js             # Service Worker, PAC engine, kết nối SSE, xử lý định tuyến
│   ├── content.js                # Đồng bộ phiên SSO từ cổng web vào Extension
│   ├── popup.html / popup.js     # Giao diện pop-up khi bấm vào biểu tượng Extension
│   └── icons/                    # Biểu tượng VNU VAGS các kích cỡ (16, 32, 48, 128)
│
├── extension-firefox/            # Dành cho Mozilla Firefox
│   ├── manifest.json             # Firefox manifest (browser_specific_settings, gecko id)
│   ├── background.js             # Xử lý proxy qua browser.proxy.onRequest
│   ├── content.js                # Đồng bộ phiên SSO
│   ├── popup.html / popup.js     # Giao diện pop-up
│   └── icons/
```

---

## 3. CƠ CHẾ CẤU HÌNH TÊN MIỀN TRONG EXTENSION

### 3.1. Điểm Khởi Tạo Tên Miền
Trong mã nguồn Extension, địa chỉ của Cổng VAGS được xác định bởi hằng số `VAGS_BASE_URL`:
* `extension-chrome/background.js` (dòng 2): `const VAGS_BASE_URL = "https://vags.vnu.edu.vn";`
* `extension-chrome/popup.js` (dòng 2): `const VAGS_BASE_URL = "https://vags.vnu.edu.vn";`
* `extension-firefox/background.js` (dòng 4): `const VAGS_BASE_URL = "https://vags.vnu.edu.vn";`
* `extension-firefox/popup.js` (dòng 2): `const VAGS_BASE_URL = "https://vags.vnu.edu.vn";`

### 3.2. Đổi Tên Miền Tự Động Bằng Script (Khuyên Dùng)
Để đổi tên miền máy chủ (ví dụ sang `gateway.vnu.edu.vn`), người vận hành hoặc Agent chỉ cần chạy:
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh set <domain_moi>
```
Script sẽ:
1. Tự động thay thế `VAGS_BASE_URL` trong toàn bộ file JS, HTML và `manifest.json`.
2. Tự động đóng gói lại các gói file `.zip` phục vụ tải về.
3. Cập nhật `vags-extension.xml` phục vụ cài đặt tự động qua Chrome Enterprise.

### 3.3. Đóng Gói Lại Thủ Công Khi Chỉnh Sửa Mã Nguồn
Nếu lập trình viên chỉnh sửa giao diện hoặc tính năng trong `extension-chrome/` hoặc `extension-firefox/`, chạy lệnh sau để đóng gói lại các file tải về trên cổng web:
```bash
/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh package-ext
```
Lệnh này sẽ cập nhật các file tải về tại `/mnt/data/websites/vags.vnu.edu.vn/public/downloads/`:
* `vnu-academic-gateway.zip` (Chrome)
* `vnu-academic-gateway-firefox.zip` (Firefox)

---

## 4. HƯỚNG DẪN TRIỂN KHAI CHO GOOGLE WORKSPACE ĐHQGHN

Để tự động cài đặt Extension cho toàn bộ cán bộ mà họ không cần cài thủ công:

### 4.1. Chuẩn Bị Thông Tin Cấu Hình
* **Extension ID:** `cdcelapgfohcgmcdkkjplcejhnmknoch`
* **Update URL:** `https://vags.vnu.edu.vn/downloads/vags-extension.xml` (hoặc `https://vags.vnu.edu.vn/updates/vags-extension.xml`)
* **File CRX cài đặt:** `https://vags.vnu.edu.vn/downloads/vnu-academic-gateway.crx`

### 4.2. Các Bước Cấu Hình Trên Google Admin Console
1. Đăng nhập vào trang quản trị: **admin.google.com** bằng tài khoản Super Admin của ĐHQGHN.
2. Điều hướng tới: **Thiết bị (Devices) $\rightarrow$ Chrome $\rightarrow$ Ứng dụng và tiện ích mở rộng (Apps & extensions) $\rightarrow$ Người dùng và trình duyệt (Users & browsers)**.
3. Chọn Đơn vị tổ chức (OU) cần triển khai:
   * Chọn OU: **Cán bộ** (`/CanBo` hoặc `/GiangVien`).
4. Nhấp vào nút màu vàng dấu **+** ở góc dưới bên phải $\rightarrow$ Chọn **Thêm bằng ID (Add by ID)**.
5. Điền thông số:
   * **Extension ID:** `cdcelapgfohcgmcdkkjplcejhnmknoch`
   * **Từ URL tùy chỉnh (From a custom URL):** `https://vags.vnu.edu.vn/downloads/vags-extension.xml`
6. Tại cột **Chính sách cài đặt (Installation policy)**, chọn:
   * **Cài đặt bắt buộc + Ghim vào thanh công cụ (Force install + pin to browser toolbar)**.
7. Nhấn **Lưu (Save)**.
* **Kết quả:** Trong vòng vài phút, toàn bộ trình duyệt Chrome của Cán bộ khi đăng nhập bằng email `@vnu.edu.vn` sẽ tự động xuất hiện tiện ích VAGS mà không cần thao tác cài đặt.

