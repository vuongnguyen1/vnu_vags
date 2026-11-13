# BÁO CÁO KỸ THUẬT: DI CHUYỂN HỆ THỐNG VAGS & TỐI ƯU HẠ TẦNG 1GBPS
## VNU Academic Gateway (VAGS) - Dedicated Infrastructure Specification

> **Máy chủ Triển khai:** `112.137.142.30` (Dedicated Ubuntu 24.04 LTS Server)  
> **Cơ quan chủ quản:** Đại học Quốc gia Hà Nội (ĐHQGHN)  
> **Đơn vị chủ trì:** Trung tâm Quản trị Đại học số (ĐHQGHN)  
> **Thời điểm hoàn thành:** 12/11/2026

---

## 1. BỐI CẢNH VÀ MỤC TIÊU DI CHUYỂN

### 1.1. Hiện trạng Máy chủ Cũ (aaPanel Server - 112.137.133.14)
* **Chia sẻ tài nguyên quá tải:** Chạy chung với Nginx, MySQL/PostgreSQL, hàng chục website và ứng dụng nội bộ khác của trường.
* **Xung đột cổng 443:** Cổng tiêu chuẩn `443` bị aaPanel Nginx chiếm giữ độc quyền, buộc VAGS phải lắng nghe tại cổng phụ `8443` hoặc dùng kỹ thuật SNI multiplexing phụ thuộc.
* **Nghẽn cổ chai khi cao điểm:** Băng thông và CPU/RAM bị phân mảnh, không đáp ứng được mục tiêu phục vụ đồng thời cho hơn 6,000 cán bộ, nhà khoa học toàn ĐHQGHN khi truy cập tra cứu tài liệu học thuật.

### 1.2. Mục Tiêu của Máy Chủ Chuyên Dụng Mới (112.137.142.30)
1. **Độc lập 100%:** Không cài đặt aaPanel hay các panel web trung gian để loại bỏ toàn bộ overhead tiêu tốn RAM/CPU.
2. **Làm chủ cổng 80 & 443:** HAProxy Ingress lắng nghe trực tiếp tại cổng tiêu chuẩn `80` (HTTP redirect 301) và `443` (HTTPS TLS + CONNECT proxy) cùng `8443` cho tương thích ngược.
3. **Tối ưu tốc độ truyền tải 1Gbps Wire-Speed:** Tinh chỉnh nhân Linux, thuật toán Google BBR, hàng đợi somaxconn 65535 và giới hạn 1,000,000 file descriptors.
4. **Phân vùng ổ đĩa chuẩn:** Tách biệt rõ ràng giữa 60GB ổ SSD hệ điều hành và 150GB ổ SAS/SSD dữ liệu chuyên dụng.

---

## 2. QUY HOẠCH & CẤU HÌNH PHÂN VÙNG Ổ ĐĨA

Hệ thống lưu trữ trên máy chủ `112.137.142.30` được tổ chức như sau:

| Phân vùng | Thiết bị vật lý | Điểm gắn (Mount Point) | Dung lượng | Mục đích sử dụng |
| :--- | :--- | :--- | :--- | :--- |
| **OS Root** | `/dev/sda` (SSD) | `/` | **57 GB** (sau mở rộng LVM) | Nhân Linux Ubuntu 24.04, thư viện hệ thống, tiện ích quản trị |
| **Data Storage** | `/dev/sdb1` (SAS/SSD) | `/mnt/data` | **147 GB** (Khả dụng >140GB) | Toàn bộ mã nguồn, Docker runtime, CSDL SQLite, file logs |

### 2.1. Mở rộng LVM Phân vùng `/` (60GB SSD)
Hệ điều hành ban đầu chỉ phân bổ ~29GB cho Logical Volume. Đã thực hiện mở rộng tối đa dung lượng khả dụng trên Volume Group:
```bash
sudo lvextend -l +100%FREE /dev/mapper/ubuntu--vg-ubuntu--lv -r
```
* Kết quả: Phân vùng `/` đạt **57 GB**, dung lượng trống đạt **48 GB** (mức sử dụng chỉ 13%).

### 2.2. Gắn Cố Định Phân Vùng Dữ Liệu `/mnt/data` (150GB)
Phân vùng `/dev/sdb1` được định dạng Ext4 và mount tự động trong `/etc/fstab`:
```fstab
UUID=e7465360-15cb-42a9-b3a1-12ec3d1cb590 /mnt/data ext4 defaults 0 0
```
Quyền sở hữu được phân bổ cho user vận hành:
```bash
sudo chown -R vags:vags /mnt/data
```

### 2.3. Chuyển Đổi Docker Data-Root Về Ổ Dữ Liệu
Để ngăn ngừa tình trạng Docker image/container làm đầy ổ hệ điều hành `/`, file `/etc/docker/daemon.json` đã được cấu hình:
```json
{
  "data-root": "/mnt/data/docker",
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "50m",
    "max-file": "5"
  }
}
```
* Kiểm tra xác nhận: `docker info | grep "Docker Root Dir"` $\rightarrow$ `/mnt/data/docker`.

---

## 3. TINH CHỈNH NHÂN LINUX (KERNEL 1GBPS HARDENING)

File cấu hình tối ưu mạng được lưu tại `/etc/sysctl.d/99-vags-1gbps.conf`:

```ini
# Bật thuật toán điều khiển tắc nghẽn Google BBR cho đường truyền 1Gbps
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr

# Mở rộng hàng đợi kết nối (Chống nghẽn SYN Flood và cao điểm truy cập)
net.core.somaxconn = 65535
net.ipv4.tcp_max_syn_backlog = 65535
net.core.netdev_max_backlog = 65535

# Tối ưu kích thước Buffer truyền nhận mạng (Tối đa 16MB)
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.ipv4.tcp_rmem = 4096 87380 16777216
net.ipv4.tcp_wmem = 4096 65536 16777216

# Tối ưu vòng đời kết nối TCP TIME_WAIT
net.ipv4.tcp_tw_reuse = 1
net.ipv4.tcp_fin_timeout = 15

# Mở rộng dải cổng kết nối Egress (Outbound Ephemeral Ports)
net.ipv4.ip_local_port_range = 1024 65535

# Cho phép container Docker bind cổng < 1024 an toàn
net.ipv4.ip_unprivileged_port_start = 0

# Tối ưu bộ nhớ ảo và swap
vm.swappiness = 10
vm.max_map_count = 262144
```

### 3.1. Nâng Giới Hạn File Descriptors (`/etc/security/limits.conf`)
```ini
* soft nofile 1048576
* hard nofile 1048576
root soft nofile 1048576
root hard nofile 1048576
vags soft nofile 1048576
vags hard nofile 1048576
```

---

## 4. BẢO MẬT TƯỜNG LỬA (UFW FIREWALL)

Tường lửa `ufw` đã được bật và chỉ cho phép lưu lượng cần thiết:
```bash
sudo ufw allow 22/tcp    comment 'SSH Remote Management'
sudo ufw allow 80/tcp    comment 'HTTP Ingress (Redirect to HTTPS)'
sudo ufw allow 443/tcp   comment 'HTTPS Standard Ingress & Proxy'
sudo ufw allow 8443/tcp  comment 'HTTPS Fallback/PAC Ingress'
sudo ufw allow 5030/tcp  comment 'Internal Kestrel HTTP'
sudo ufw allow 5031/tcp  comment 'Internal Kestrel HTTPS'
sudo ufw enable
```

---

## 5. THÔNG SỐ VẬN HÀNH THỰC TẾ TRÊN MÁY CHỦ MỚI

* **Bộ vi xử lý:** Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz (Skylake-SP kiến trúc máy chủ doanh nghiệp).
* **Bộ nhớ RAM:** 15.6 GiB (Mức sử dụng trung bình: ~600 MiB, khả dụng >15 GiB).
* **Độ trễ trung bình:** < 1.2ms trong mạng ĐHQGHN.
* **Khả năng chịu tải đồng thời:** $\ge 25,000$ kết nối TCP song song qua HAProxy (4 luồng `nbthread 4`).

