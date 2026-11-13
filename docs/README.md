# THƯ MỤC TÀI LIỆU HỆ THỐNG VAGS (SYSTEM DOCUMENTATION)
## VNU Academic Gateway (VAGS) - Knowledge Base & Technical Docs

> **Đơn vị chủ trì:** Trung tâm Quản trị Đại học số (ĐHQGHN)  
> **Máy chủ vận hành:** Node Chuyên dụng `112.137.142.30` (Ubuntu 24.04 LTS, 1Gbps BBR)  
> **Thư mục lưu trữ:** `/mnt/data/websites/vags.vnu.edu.vn/docs/`

---

## 📚 DANH MỤC TÀI LIỆU HỆ THỐNG (DOCUMENTATION INDEX)

Thư mục này tập hợp toàn bộ hồ sơ kỹ thuật, kiến trúc thiết kế, hướng dẫn phát triển và quy trình vận hành dành cho **Đội ngũ Kỹ thuật**, **SysAdmin/DevOps**, và các **AI Agent** tiếp quản hệ thống:

| Tên Tài Liệu | File Liên Kết | Đối Tượng Sử Dụng | Tóm Tắt Nội Dung |
| :--- | :--- | :--- | :--- |
| **1. Hướng Dẫn Kỹ Thuật & AI Agent** | [`DEVELOPER_AGENT_INSTRUCTIONS.md`](./DEVELOPER_AGENT_INSTRUCTIONS.md) | AI Agents, Backend Developers | Cẩm nang kỹ thuật cốt lõi: Kiến trúc luồng mạng, quy ước mã nguồn, hướng dẫn các AI Agent khi bảo trì/nâng cấp không làm hỏng dữ liệu SQLite và chứng chỉ SSL. |
| **2. Thiết Kế Kiến Trúc Hệ Thống (Master Spec)** | [`vags-system-design.md`](./vags-system-design.md) | System Architects, Tech Leads | Hồ sơ thiết kế tổng thể chi tiết (High-level architecture, ma trận công nghệ .NET 8/Kestrel, HAProxy SNI multiplexer, bảo mật chống rò rỉ IP, sequence diagrams). |
| **3. Báo Cáo Di Chuyển & Hạ Tầng 1Gbps** | [`vags_migration_and_1gbps_tuning.md`](./vags_migration_and_1gbps_tuning.md) | SysAdmin, SRE, DevOps | Chi tiết quá trình tách khỏi aaPanel sang máy chủ chuyên dụng `112.137.142.30`, mở rộng LVM ổ 60GB, cấu hình ổ 150GB `/mnt/data`, tinh chỉnh nhân Linux Google BBR 1Gbps và UFW. |
| **4. Cẩm Nang Browser Extension** | [`extension-guide.md`](./extension-guide.md) | Frontend Devs, Google Workspace Admin | Cấu trúc mã nguồn Chrome/Firefox Extension (Manifest V3), cơ chế PAC script, cách đổi domain tự động, và quy trình phân phối tự động hàng loạt qua Google Workspace Admin Console. |
| **5. Sổ Tay Vận Hành & Sự Cố (Runbook)** | [`operations-runbook.md`](./operations-runbook.md) | Vận hành viên (Ops), SysAdmin | Bảng tra cứu lệnh nhanh, quy trình đóng cổng bảo trì khẩn cấp (`switch-domain.sh close`), mở cổng (`open`), khoá tài khoản vi phạm và sao lưu CSDL SQLite WAL. |

---

## 🧭 HƯỚNG DẪN ĐỊNH VỊ CHO CÁC AI AGENT TƯƠNG LAI

Khi nhận được yêu cầu nâng cấp, kiểm tra hoặc sửa lỗi hệ thống VAGS:
1. **Muốn hiểu tổng quan kiến trúc luồng dữ liệu:** Đọc [`DEVELOPER_AGENT_INSTRUCTIONS.md`](./DEVELOPER_AGENT_INSTRUCTIONS.md).
2. **Muốn đổi tên miền hoặc đóng/mở cổng bảo trì:** Sử dụng công cụ tự động hóa đã xây dựng sẵn tại `/mnt/data/websites/vags.vnu.edu.vn/scripts/switch-domain.sh` (Tham khảo [`operations-runbook.md`](./operations-runbook.md)).
3. **Muốn chỉnh sửa Extension hoặc phân phối lại cho cán bộ:** Đọc [`extension-guide.md`](./extension-guide.md).
4. **Muốn kiểm tra thông số mạng 1Gbps, phân vùng ổ đĩa hoặc Docker root:** Đọc [`vags_migration_and_1gbps_tuning.md`](./vags_migration_and_1gbps_tuning.md).
5. **Dữ liệu SQLite:** Luôn bảo toàn thư mục `/mnt/data/websites/vags.vnu.edu.vn/data/` (chứa `vags_monitor.db` và `rules.json`).

---
*© 2026 Trung tâm Quản trị Đại học số - Đại học Quốc gia Hà Nội. Tài liệu lưu hành nội bộ.*

