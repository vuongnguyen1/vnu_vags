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
    output_path = data.get('output_path', '/tmp/vags_csdl_report.xlsx')

    # Case-insensitive lookup dicts
    matrix_lookup = {str(k).strip().upper(): v for k, v in matrix.items()}
    unit_users_lookup = {str(k).strip().upper(): int(v) for k, v in by_unit_users.items()}

    wb = openpyxl.Workbook()

    # =========================================================================
    # SHEET 1: BÁO CÁO THỐNG KÊ 4 CSDL ĐIỆN TỬ (Bắt đầu từ Cột A)
    # =========================================================================
    ws1 = wb.active
    ws1.title = "Thong_Ke_4_CSDL"
    ws1.views.sheetView[0].showGridLines = True

    # Styling constants
    DARK_GREEN = "00803d"
    TOTAL_GREEN = "dcfce7"
    SD_BG = "fff7ed"
    SP_BG = "f0f9ff"
    MS_BG = "fef2f2"
    PQ_BG = "faf5ff"
    BORDER_GRAY = "cbd5e1"
    HEADER_GRAY = "f8fafc"

    font_org = Font(name="Times New Roman", size=11, bold=True, color="334155")
    font_title = Font(name="Times New Roman", size=13, bold=True, color="000000")
    font_sub = Font(name="Times New Roman", size=10, italic=True, color="475569")
    font_header = Font(name="Times New Roman", size=10, bold=True, color="0f172a")
    font_data = Font(name="Times New Roman", size=10, bold=False, color="000000")
    font_data_bold = Font(name="Times New Roman", size=10, bold=True, color="000000")
    font_sum = Font(name="Times New Roman", size=11, bold=True, color="000000")

    thin_side = Side(border_style="thin", color=BORDER_GRAY)
    med_side = Side(border_style="medium", color="0f172a")
    double_side = Side(border_style="double", color="0f172a")

    border_cell = Border(left=thin_side, right=thin_side, top=thin_side, bottom=thin_side)
    border_header = Border(left=thin_side, right=thin_side, top=thin_side, bottom=med_side)
    border_total = Border(left=thin_side, right=thin_side, top=med_side, bottom=double_side)

    align_center = Alignment(horizontal="center", vertical="center", wrap_text=True)
    align_left = Alignment(horizontal="left", vertical="center")
    align_right = Alignment(horizontal="right", vertical="center")

    # Titles (Rows 1 - 3)
    ws1.merge_cells("A1:M1")
    ws1["A1"] = "ĐẠI HỌC QUỐC GIA HÀ NỘI — TRUNG TÂM QUẢN TRỊ ĐẠI HỌC SỐ (DUAC)"
    ws1["A1"].font = font_org
    ws1["A1"].alignment = align_center

    ws1.merge_cells("A2:M2")
    ws1["A2"] = title
    ws1["A2"].font = font_title
    ws1["A2"].alignment = align_center

    ws1.merge_cells("A3:M3")
    ws1["A3"] = "Hệ thống Cổng Học Thuật VAGS • Phân tuyến an toàn qua Egress IP: 112.137.142.30"
    ws1["A3"].font = font_sub
    ws1["A3"].alignment = align_center

    ws1.row_dimensions[1].height = 20
    ws1.row_dimensions[2].height = 26
    ws1.row_dimensions[3].height = 18
    ws1.row_dimensions[4].height = 8

    # Table Headers (Rows 5 & 6)
    ws1.merge_cells("A5:A6")
    ws1["A5"] = "STT"
    ws1.merge_cells("B5:B6")
    ws1["B5"] = "Mã ĐV"
    ws1.merge_cells("C5:C6")
    ws1["C5"] = "Tên Đơn Vị Thành Viên"

    ws1.merge_cells("D5:E5")
    ws1["D5"] = "ScienceDirect (Elsevier)"
    ws1["D6"] = "Người học"
    ws1["E6"] = "Cán bộ"

    ws1.merge_cells("F5:G5")
    ws1["F5"] = "Springer Link (Nature)"
    ws1["F6"] = "Người học"
    ws1["G6"] = "Cán bộ"

    ws1.merge_cells("H5:I5")
    ws1["H5"] = "MathSciNet (AMS)"
    ws1["H6"] = "Người học"
    ws1["I6"] = "Cán bộ"

    ws1.merge_cells("J5:K5")
    ws1["J5"] = "ProQuest (Clarivate)"
    ws1["J6"] = "Người học"
    ws1["K6"] = "Cán bộ"

    ws1.merge_cells("L5:L6")
    ws1["L5"] = "Tổng Lượt\n(Requests)"

    ws1.merge_cells("M5:M6")
    ws1["M5"] = "Số Users\n(Người dùng)"

    header_fills = {
        1: HEADER_GRAY, 2: HEADER_GRAY, 3: HEADER_GRAY,
        4: SD_BG, 5: SD_BG,
        6: SP_BG, 7: SP_BG,
        8: MS_BG, 9: MS_BG,
        10: PQ_BG, 11: PQ_BG,
        12: TOTAL_GREEN, 13: TOTAL_GREEN
    }

    for r in (5, 6):
        ws1.row_dimensions[r].height = 24
        for c in range(1, 14):
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
        sd_hoc = int(sd.get('nguoihoc', 0))
        sd_bo = int(sd.get('canbo', 0))
        ws1.cell(r, 4, sd_hoc)
        ws1.cell(r, 5, sd_bo)

        # Springer Link
        sp = u_data.get('Springer Link', u_data.get('Springer', {}))
        sp_hoc = int(sp.get('nguoihoc', 0))
        sp_bo = int(sp.get('canbo', 0))
        ws1.cell(r, 6, sp_hoc)
        ws1.cell(r, 7, sp_bo)

        # MathSciNet
        ms = u_data.get('MathSciNet', {})
        ms_hoc = int(ms.get('nguoihoc', 0))
        ms_bo = int(ms.get('canbo', 0))
        ws1.cell(r, 8, ms_hoc)
        ws1.cell(r, 9, ms_bo)

        # ProQuest
        pq = u_data.get('ProQuest', {})
        pq_hoc = int(pq.get('nguoihoc', 0))
        pq_bo = int(pq.get('canbo', 0))
        ws1.cell(r, 10, pq_hoc)
        ws1.cell(r, 11, pq_bo)

        # Col L (12): Total requests = SUM(D{r}:K{r})
        col_l = ws1.cell(r, 12, f"=SUM(D{r}:K{r})")
        col_l.alignment = align_right
        col_l.font = font_data_bold
        col_l.number_format = "#,##0"

        # Col M (13): Distinct Users for this unit
        u_users = unit_users_lookup.get(code.upper(), 0)
        if u_users == 0:
            row_total = sd_hoc + sd_bo + sp_hoc + sp_bo + ms_hoc + ms_bo + pq_hoc + pq_bo
            u_users = max(1 if row_total > 0 else 0, int(sd.get('users', 0)) + int(sp.get('users', 0)) + int(ms.get('users', 0)) + int(pq.get('users', 0)))
        col_m = ws1.cell(r, 13, u_users)
        col_m.alignment = align_right
        col_m.font = font_data_bold
        col_m.number_format = "#,##0"

        for c in range(1, 14):
            ws1.cell(r, c).border = border_cell
            if c not in (2, 12, 13):
                ws1.cell(r, c).font = font_data
            if c in range(4, 12):
                ws1.cell(r, c).alignment = align_right
                ws1.cell(r, c).number_format = "#,##0"

    # Row 29: TỔNG CỘNG
    r_total = 29
    ws1.row_dimensions[r_total].height = 26
    ws1.merge_cells("A29:C29")
    ws1["A29"] = "TỔNG CỘNG"
    ws1["A29"].alignment = align_center
    ws1["A29"].font = font_sum

    for c in range(4, 14):
        col_letter = get_column_letter(c)
        cell = ws1.cell(r_total, c, f"=SUM({col_letter}7:{col_letter}28)")
        cell.alignment = align_right
        cell.font = font_sum
        cell.number_format = "#,##0"

    for c in range(1, 14):
        cell = ws1.cell(r_total, c)
        cell.border = border_total
        cell.fill = PatternFill(start_color=TOTAL_GREEN, end_color=TOTAL_GREEN, fill_type="solid")

    # Column widths for Sheet 1
    col_widths = {
        1: 6,
        2: 10,
        3: 38,
        4: 13,
        5: 13,
        6: 13,
        7: 13,
        8: 13,
        9: 13,
        10: 13,
        11: 13,
        12: 16,
        13: 16
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
