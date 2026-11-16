#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys
import json
import os
import openpyxl
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.utils import get_column_letter

CANONICAL_UNITS = [
    ("HUS", "Trường Đại học Khoa học Tự nhiên"),
    ("USSH", "Trường Đại học Khoa học Xã hội và Nhân văn"),
    ("ULIS", "Trường Đại học Ngoại ngữ"),
    ("UET", "Trường Đại học Công nghệ"),
    ("UEd", "Trường Đại học Giáo dục"),
    ("UEB", "Trường Đại học Kinh tế"),
    ("UMP", "Trường Đại học Y Dược"),
    ("VJU", "Trường Đại học Việt Nhật"),
    ("UL", "Trường Đại học Luật"),
    ("IS", "Trường Quốc tế"),
    ("HSB", "Trường Quản trị và Kinh doanh"),
    ("SIS", "Trường Khoa học liên ngành và Nghệ thuật"),
    ("ITI", "Viện Công nghệ Thông tin"),
    ("CRES", "Viện Tài nguyên và Môi trường"),
    ("IMBT", "Viện Vi sinh vật và Công nghệ Sinh học"),
    ("IVIDES", "Viện Đảm bảo chất lượng giáo dục"),
    ("TNTI", "Viện Trần Nhân Tông"),
    ("TIP", "Viện Đào tạo Quốc tế và Khoa học ứng dụng"),
    ("IQA", "Viện Nghiên cứu Phát triển Giáo dục"),
    ("CEA", "Trung tâm Kiểm định chất lượng giáo dục"),
    ("LIC", "Trung tâm Thư viện và Tri thức số"),
    ("OTHER", "Cơ quan ĐHQGHN và Đơn vị khác")
]

DATABASE_PACKAGES = [
    (1, "ScienceDirect", "https://www.sciencedirect.com", "Elsevier", "Trung tâm Thư viện và Tri thức số (LIC)", "2026", "Bản quyền truy cập toàn ĐHQGHN"),
    (2, "Springer Link & Nature", "https://link.springer.com", "Springer Nature", "Trung tâm Thư viện và Tri thức số (LIC)", "2026", "Bản quyền truy cập toàn ĐHQGHN"),
    (3, "MathSciNet", "https://mathscinet.ams.org", "American Mathematical Society (AMS)", "Trung tâm Thư viện và Tri thức số (LIC)", "2026", "Bản quyền truy cập toàn ĐHQGHN"),
    (4, "ProQuest Central", "https://www.proquest.com", "Clarivate / ProQuest", "Trung tâm Thư viện và Tri thức số (LIC)", "2026", "Bản quyền truy cập toàn ĐHQGHN")
]

def generate_excel(input_json_path):
    with open(input_json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    title = data.get('title')
    from_date = data.get('from_date', '08/11/2026')
    to_date = data.get('to_date', '16/11/2026')
    if not title:
        title = f"THỐNG KÊ SỐ LƯỢNG TRUY CẬP CSDL ĐIỆN TỬ TỪ NGÀY {from_date} ĐẾN NGÀY {to_date}"

    matrix = data.get('matrix', {})
    by_unit_users = data.get('by_unit_users', {})
    by_unit_hoc_users = data.get('by_unit_hoc_users', {})
    by_unit_bo_users = data.get('by_unit_bo_users', {})
    by_database_stats = data.get('by_database_stats', {})
    by_member_type_users = data.get('by_member_type_users', {})
    unique_users_val = int(data.get('unique_users', 0))

    output_path = data.get('output_path', '/tmp/vags_csdl_report.xlsx')

    # Case-insensitive lookup dicts
    matrix_lookup = {str(k).strip().upper(): v for k, v in matrix.items()}
    unit_users_lookup = {str(k).strip().upper(): int(v) for k, v in by_unit_users.items()}
    unit_hoc_users_lookup = {str(k).strip().upper(): int(v) for k, v in by_unit_hoc_users.items()}
    unit_bo_users_lookup = {str(k).strip().upper(): int(v) for k, v in by_unit_bo_users.items()}

    wb = openpyxl.Workbook()

    # =========================================================================
    # SHEET 1: BÁO CÁO THỐNG KÊ 4 CSDL ĐIỆN TỬ (Bắt đầu từ Cột A, 25 cột chuẩn)
    # =========================================================================
    ws1 = wb.active
    ws1.title = "Thong_Ke_4_CSDL"
    ws1.views.sheetView[0].showGridLines = True

    # Styling constants
    SD_BG = "fff7ed"
    SP_BG = "f0f9ff"
    MS_BG = "fef2f2"
    PQ_BG = "faf5ff"
    HOC_BG = "eff6ff"
    BO_BG = "f0fdf4"
    TOTAL_GREEN = "dcfce7"
    BORDER_GRAY = "cbd5e1"
    HEADER_GRAY = "f8fafc"

    font_org = Font(name="Times New Roman", size=11, bold=True, color="334155")
    font_title = Font(name="Times New Roman", size=13, bold=True, color="000000")
    font_sub = Font(name="Times New Roman", size=10, italic=True, color="475569")
    font_header = Font(name="Times New Roman", size=10, bold=True, color="0f172a")
    font_data = Font(name="Times New Roman", size=10, bold=False, color="000000")
    font_data_bold = Font(name="Times New Roman", size=10, bold=True, color="000000")
    font_sum = Font(name="Times New Roman", size=10, bold=True, color="000000")

    thin_side = Side(border_style="thin", color=BORDER_GRAY)
    med_side = Side(border_style="medium", color="0f172a")
    double_side = Side(border_style="double", color="0f172a")

    border_cell = Border(left=thin_side, right=thin_side, top=thin_side, bottom=thin_side)
    border_header = Border(left=thin_side, right=thin_side, top=thin_side, bottom=med_side)
    border_total = Border(left=thin_side, right=thin_side, top=med_side, bottom=double_side)

    align_center = Alignment(horizontal="center", vertical="center", wrap_text=True)
    align_left = Alignment(horizontal="left", vertical="center")
    align_right = Alignment(horizontal="right", vertical="center")

    # Titles (Rows 1 - 3, merged across all 25 columns A to Y)
    ws1.merge_cells("A1:Y1")
    ws1["A1"] = "ĐẠI HỌC QUỐC GIA HÀ NỘI — TRUNG TÂM QUẢN TRỊ ĐẠI HỌC SỐ (DUAC)"
    ws1["A1"].font = font_org
    ws1["A1"].alignment = align_center

    ws1.merge_cells("A2:Y2")
    ws1["A2"] = title
    ws1["A2"].font = font_title
    ws1["A2"].alignment = align_center

    ws1.merge_cells("A3:Y3")
    ws1["A3"] = "Hệ thống Cổng Học Thuật VAGS • Phân tuyến an toàn qua Egress IP: 112.137.142.30"
    ws1["A3"].font = font_sub
    ws1["A3"].alignment = align_center

    ws1.row_dimensions[1].height = 20
    ws1.row_dimensions[2].height = 26
    ws1.row_dimensions[3].height = 18
    ws1.row_dimensions[4].height = 8

    # Table Headers Tier 1 (Row 5)
    ws1.merge_cells("A5:A6")
    ws1["A5"] = "STT"
    ws1.merge_cells("B5:B6")
    ws1["B5"] = "Mã ĐV"
    ws1.merge_cells("C5:C6")
    ws1["C5"] = "Tên Đơn Vị Thành Viên"

    ws1.merge_cells("D5:G5")
    ws1["D5"] = "ScienceDirect (Elsevier)"

    ws1.merge_cells("H5:K5")
    ws1["H5"] = "Springer Link (Nature)"

    ws1.merge_cells("L5:O5")
    ws1["L5"] = "MathSciNet (AMS)"

    ws1.merge_cells("P5:S5")
    ws1["P5"] = "ProQuest (Clarivate)"

    ws1.merge_cells("T5:U5")
    ws1["T5"] = "Tổng Người học"

    ws1.merge_cells("V5:W5")
    ws1["V5"] = "Tổng Cán bộ"

    ws1.merge_cells("X5:Y5")
    ws1["X5"] = "TỔNG TOÀN ĐƠN VỊ"

    # Table Headers Tier 2 (Row 6)
    sub_headers = {
        4: "Học (Req)", 5: "Học (User)", 6: "Cán bộ (Req)", 7: "Cán bộ (User)",
        8: "Học (Req)", 9: "Học (User)", 10: "Cán bộ (Req)", 11: "Cán bộ (User)",
        12: "Học (Req)", 13: "Học (User)", 14: "Cán bộ (Req)", 15: "Cán bộ (User)",
        16: "Học (Req)", 17: "Học (User)", 18: "Cán bộ (Req)", 19: "Cán bộ (User)",
        20: "Tổng Req", 21: "Số User",
        22: "Tổng Req", 23: "Số User",
        24: "Tổng Reqs", 25: "Số Users"
    }
    for c_idx, h_name in sub_headers.items():
        ws1.cell(6, c_idx, h_name)

    header_fills = {
        1: HEADER_GRAY, 2: HEADER_GRAY, 3: HEADER_GRAY,
        4: SD_BG, 5: SD_BG, 6: SD_BG, 7: SD_BG,
        8: SP_BG, 9: SP_BG, 10: SP_BG, 11: SP_BG,
        12: MS_BG, 13: MS_BG, 14: MS_BG, 15: MS_BG,
        16: PQ_BG, 17: PQ_BG, 18: PQ_BG, 19: PQ_BG,
        20: HOC_BG, 21: HOC_BG,
        22: BO_BG, 23: BO_BG,
        24: TOTAL_GREEN, 25: TOTAL_GREEN
    }

    for r in (5, 6):
        ws1.row_dimensions[r].height = 24
        for c in range(1, 26):
            cell = ws1.cell(r, c)
            cell.font = font_header
            cell.alignment = align_center
            cell.border = border_header
            fill_color = header_fills.get(c, HEADER_GRAY)
            cell.fill = PatternFill(start_color=fill_color, end_color=fill_color, fill_type="solid")

    # Data rows: 22 Canonical Units (Rows 7 to 28)
    for idx, (code, full_name) in enumerate(CANONICAL_UNITS, start=1):
        r = 6 + idx
        ws1.row_dimensions[r].height = 21

        ws1.cell(r, 1, idx).alignment = align_center
        ws1.cell(r, 2, code).alignment = align_center
        ws1.cell(r, 2).font = font_data_bold
        ws1.cell(r, 3, full_name).alignment = align_left

        u_data = matrix_lookup.get(code.upper(), matrix.get(code, {}))

        # ScienceDirect
        sd = u_data.get('ScienceDirect', {})
        sd_hoc_req = int(sd.get('nguoihoc', 0))
        sd_hoc_usr = int(sd.get('nguoihoc_users', 0))
        sd_bo_req = int(sd.get('canbo', 0))
        sd_bo_usr = int(sd.get('canbo_users', 0))
        ws1.cell(r, 4, sd_hoc_req)
        ws1.cell(r, 5, sd_hoc_usr)
        ws1.cell(r, 6, sd_bo_req)
        ws1.cell(r, 7, sd_bo_usr)

        # Springer Link
        sp = u_data.get('Springer Link', u_data.get('Springer', {}))
        sp_hoc_req = int(sp.get('nguoihoc', 0))
        sp_hoc_usr = int(sp.get('nguoihoc_users', 0))
        sp_bo_req = int(sp.get('canbo', 0))
        sp_bo_usr = int(sp.get('canbo_users', 0))
        ws1.cell(r, 8, sp_hoc_req)
        ws1.cell(r, 9, sp_hoc_usr)
        ws1.cell(r, 10, sp_bo_req)
        ws1.cell(r, 11, sp_bo_usr)

        # MathSciNet
        ms = u_data.get('MathSciNet', {})
        ms_hoc_req = int(ms.get('nguoihoc', 0))
        ms_hoc_usr = int(ms.get('nguoihoc_users', 0))
        ms_bo_req = int(ms.get('canbo', 0))
        ms_bo_usr = int(ms.get('canbo_users', 0))
        ws1.cell(r, 12, ms_hoc_req)
        ws1.cell(r, 13, ms_hoc_usr)
        ws1.cell(r, 14, ms_bo_req)
        ws1.cell(r, 15, ms_bo_usr)

        # ProQuest
        pq = u_data.get('ProQuest', {})
        pq_hoc_req = int(pq.get('nguoihoc', 0))
        pq_hoc_usr = int(pq.get('nguoihoc_users', 0))
        pq_bo_req = int(pq.get('canbo', 0))
        pq_bo_usr = int(pq.get('canbo_users', 0))
        ws1.cell(r, 16, pq_hoc_req)
        ws1.cell(r, 17, pq_hoc_usr)
        ws1.cell(r, 18, pq_bo_req)
        ws1.cell(r, 19, pq_bo_usr)

        # Tổng Người học: Col T (20: Reqs = D+H+L+P), Col U (21: Users)
        col_t = ws1.cell(r, 20, f"=D{r}+H{r}+L{r}+P{r}")
        col_t.font = font_data_bold
        u_hoc_users = unit_hoc_users_lookup.get(code.upper(), 0)
        col_u = ws1.cell(r, 21, u_hoc_users)
        col_u.font = font_data_bold

        # Tổng Cán bộ: Col V (22: Reqs = F+J+N+R), Col W (23: Users)
        col_v = ws1.cell(r, 22, f"=F{r}+J{r}+N{r}+R{r}")
        col_v.font = font_data_bold
        u_bo_users = unit_bo_users_lookup.get(code.upper(), 0)
        col_w = ws1.cell(r, 23, u_bo_users)
        col_w.font = font_data_bold

        # TỔNG TOÀN ĐƠN VỊ: Col X (24: Total Reqs = T+V), Col Y (25: Total Users)
        col_x = ws1.cell(r, 24, f"=T{r}+V{r}")
        col_x.font = font_data_bold
        u_tot_users = unit_users_lookup.get(code.upper(), 0)
        col_y = ws1.cell(r, 25, u_tot_users)
        col_y.font = font_data_bold

        for c in range(1, 26):
            ws1.cell(r, c).border = border_cell
            if c not in (2, 20, 21, 22, 23, 24, 25):
                ws1.cell(r, c).font = font_data
            if c in range(4, 26):
                ws1.cell(r, c).alignment = align_right
                ws1.cell(r, c).number_format = "#,##0"

    # Row 29: TỔNG CỘNG
    r_total = 29
    ws1.row_dimensions[r_total].height = 26
    ws1.merge_cells("A29:C29")
    ws1["A29"] = "TỔNG CỘNG"
    ws1["A29"].font = font_sum
    ws1["A29"].alignment = align_center

    # Database totals from backend stats (distinct users across university)
    sd_stats = by_database_stats.get('ScienceDirect', {})
    sp_stats = by_database_stats.get('Springer Link', {})
    ms_stats = by_database_stats.get('MathSciNet', {})
    pq_stats = by_database_stats.get('ProQuest', {})

    total_hoc_users = int(by_member_type_users.get('nguoihoc', 0))
    total_bo_users = int(by_member_type_users.get('canbo', 0))

    # Columns formulas / distinct counts
    tot_cells = {
        4: "=SUM(D7:D28)",
        5: int(sd_stats.get('hoc_users', 0)),
        6: "=SUM(F7:F28)",
        7: int(sd_stats.get('bo_users', 0)),
        8: "=SUM(H7:H28)",
        9: int(sp_stats.get('hoc_users', 0)),
        10: "=SUM(J7:J28)",
        11: int(sp_stats.get('bo_users', 0)),
        12: "=SUM(L7:L28)",
        13: int(ms_stats.get('hoc_users', 0)),
        14: "=SUM(N7:N28)",
        15: int(ms_stats.get('bo_users', 0)),
        16: "=SUM(P7:P28)",
        17: int(pq_stats.get('hoc_users', 0)),
        18: "=SUM(R7:R28)",
        19: int(pq_stats.get('bo_users', 0)),
        20: "=SUM(T7:T28)",
        21: total_hoc_users,
        22: "=SUM(V7:V28)",
        23: total_bo_users,
        24: "=SUM(X7:X28)",
        25: unique_users_val
    }

    for c in range(1, 26):
        cell = ws1.cell(r_total, c)
        if c in tot_cells:
            cell.value = tot_cells[c]
        cell.font = font_sum
        cell.border = border_total
        fill_color = TOTAL_GREEN if c in (24, 25) else (header_fills.get(c, HEADER_GRAY))
        cell.fill = PatternFill(start_color=fill_color, end_color=fill_color, fill_type="solid")
        if c in range(4, 26):
            cell.alignment = align_right
            cell.number_format = "#,##0"

    # Column widths (A to Y)
    col_widths = {
        1: 6,   # STT
        2: 9,   # Mã ĐV
        3: 36,  # Tên ĐV
        4: 11, 5: 11, 6: 11, 7: 11,   # SD: Hoc Req, Hoc Usr, Bo Req, Bo Usr
        8: 11, 9: 11, 10: 11, 11: 11, # SP: Hoc Req, Hoc Usr, Bo Req, Bo Usr
        12: 11, 13: 11, 14: 11, 15: 11,# MS: Hoc Req, Hoc Usr, Bo Req, Bo Usr
        16: 11, 17: 11, 18: 11, 19: 11,# PQ: Hoc Req, Hoc Usr, Bo Req, Bo Usr
        20: 12, 21: 11,               # Tot Hoc: Req, Usr
        22: 12, 23: 11,               # Tot Bo: Req, Usr
        24: 14, 25: 13                # TOÀN ĐƠN VỊ: Tot Reqs, Tot Usrs
    }
    for col_idx, width in col_widths.items():
        ws1.column_dimensions[get_column_letter(col_idx)].width = width

    # =========================================================================
    # SHEET 2: DANH MỤC CSDL BẢN QUYỀN ĐHQGHN (Thông tin gói thuê bao)
    # =========================================================================
    ws2 = wb.create_sheet(title="Danh_Muc_CSDL")
    ws2.views.sheetView[0].showGridLines = True

    ws2.merge_cells("A1:G1")
    ws2["A1"] = "DANH MỤC CƠ SỞ DỮ LIỆU ĐIỆN TỬ BẢN QUYỀN TẠI ĐHQGHN"
    ws2["A1"].font = font_title
    ws2["A1"].alignment = align_center
    ws2.row_dimensions[1].height = 24

    pkg_headers = ["STT", "Tên CSDL", "Địa Chỉ Truy Cập", "Nhà Xuất Bản", "Đơn Vị Quản Lý Mua", "Năm Mua", "Phạm Vi & Hình Thức"]
    ws2.row_dimensions[3].height = 22
    for c_idx, h_text in enumerate(pkg_headers, start=1):
        cell = ws2.cell(3, c_idx, h_text)
        cell.font = font_header
        cell.alignment = align_center
        cell.border = border_header
        cell.fill = PatternFill(start_color="e2e8f0", end_color="e2e8f0", fill_type="solid")

    for r_idx, pkg in enumerate(DATABASE_PACKAGES, start=4):
        ws2.row_dimensions[r_idx].height = 20
        for c_idx, val in enumerate(pkg, start=1):
            cell = ws2.cell(r_idx, c_idx, val)
            cell.font = font_data
            cell.border = border_cell
            if c_idx == 1:
                cell.alignment = align_center
            elif c_idx in (2, 3):
                cell.alignment = align_left
                if c_idx == 2:
                    cell.font = font_data_bold
            else:
                cell.alignment = align_left

    s2_widths = {1: 6, 2: 26, 3: 32, 4: 28, 5: 35, 6: 12, 7: 35}
    for col_idx, width in s2_widths.items():
        ws2.column_dimensions[get_column_letter(col_idx)].width = width

    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    wb.save(output_path)
    print(f"SUCCESS: {output_path}")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python3 export_csdl_excel.py <payload.json>")
        sys.exit(1)
    generate_excel(sys.argv[1])
