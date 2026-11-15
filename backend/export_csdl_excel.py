#!/usr/bin/env python3
import sys
import json
import os
import openpyxl
from openpyxl.utils import get_column_letter

def generate_excel(input_json_path):
    with open(input_json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    title = data.get('title', 'THỐNG KÊ SỐ LƯỢNG TRUY CẬP CSDL ĐIỆN TỬ')
    matrix = data.get('matrix', {})
    template_path = data.get('template_path', '/home/aapanel/Danh_muc_CSDL-dien-tu_DHQGHN.xlsx')
    output_path = data.get('output_path', '/tmp/vags_csdl_report.xlsx')

    if not os.path.exists(template_path):
        raise FileNotFoundError(f"Template not found: {template_path}")

    wb = openpyxl.load_workbook(template_path)
    ws = wb.active

    # Cập nhật ô tiêu đề bảng thống kê (H6)
    ws['H6'] = title

    # Bản đồ vị trí hàng cho 22 đơn vị (từ hàng 9 đến 30)
    # HUS, USSH, ULIS, UET, UEd, UEB, UMP, VJU, UL, IS, HSB, SIS, ITI, CRES, IMBT, IVIDES, TNTI, TIP, IQA, CEA, LIC, OTHER
    units_row_map = {}
    for r in range(9, 31):
        unit_name = ws.cell(r, 8).value
        if unit_name:
            units_row_map[str(unit_name).strip().upper()] = r

    # Tạo lookup dict không phân biệt hoa thường cho ma trận
    matrix_lookup = {k.strip().upper(): v for k, v in matrix.items()}

    # Thiết lập tiêu đề cho Cột 17 (Tổng Lượt) và Cột 18 (Số Users)
    ws.cell(7, 17, 'TỔNG CỘNG')
    ws.cell(8, 17, 'Tổng Lượt (Reqs)')
    ws.cell(7, 18, 'TỔNG CỘNG')
    ws.cell(8, 18, 'Số User (Người dùng)')

    # Cột tương ứng:
    # Col 9 (I): ScienceDirect - Người học
    # Col 10 (J): ScienceDirect - Cán bộ
    # Col 11 (K): Springer Link - Người học
    # Col 12 (L): Springer Link - Cán bộ
    # Col 13 (M): MathSciNet - Người học
    # Col 14 (N): MathSciNet - Cán bộ
    # Col 15 (O): ProQuest - Người học
    # Col 16 (P): ProQuest - Cán bộ
    # Col 17 (Q): Tổng Lượt (Requests)
    # Col 18 (R): Số User (Người dùng)
    for unit_key, r in units_row_map.items():
        u_data = matrix_lookup.get(unit_key.strip().upper(), matrix.get(unit_key, {}))
        
        # ScienceDirect
        sd = u_data.get('ScienceDirect', {})
        sd_hoc = int(sd.get('nguoihoc', 0))
        sd_bo = int(sd.get('canbo', 0))
        ws.cell(r, 9, sd_hoc)
        ws.cell(r, 10, sd_bo)

        # Springer Link
        sp = u_data.get('Springer Link', u_data.get('Springer', {}))
        sp_hoc = int(sp.get('nguoihoc', 0))
        sp_bo = int(sp.get('canbo', 0))
        ws.cell(r, 11, sp_hoc)
        ws.cell(r, 12, sp_bo)

        # MathSciNet
        ms = u_data.get('MathSciNet', {})
        ms_hoc = int(ms.get('nguoihoc', 0))
        ms_bo = int(ms.get('canbo', 0))
        ws.cell(r, 13, ms_hoc)
        ws.cell(r, 14, ms_bo)

        # ProQuest
        pq = u_data.get('ProQuest', {})
        pq_hoc = int(pq.get('nguoihoc', 0))
        pq_bo = int(pq.get('canbo', 0))
        ws.cell(r, 15, pq_hoc)
        ws.cell(r, 16, pq_bo)

        # Col 17: Tổng Lượt (Requests)
        row_total_req = sd_hoc + sd_bo + sp_hoc + sp_bo + ms_hoc + ms_bo + pq_hoc + pq_bo
        ws.cell(r, 17, row_total_req)

        # Col 18: Số Users (Người dùng độc bản)
        u_users = int(sd.get('users', 0)) + int(sp.get('users', 0)) + int(ms.get('users', 0)) + int(pq.get('users', 0))
        if 'users' in u_data and isinstance(u_data['users'], (int, float)):
            u_users = int(u_data['users'])
        ws.cell(r, 18, max(1 if row_total_req > 0 else 0, u_users))

    # Hàng 31: Công thức tính tổng cho từng cột từ I đến R
    for c in range(9, 19):
        col_letter = get_column_letter(c)
        ws.cell(31, c, f"=SUM({col_letter}9:{col_letter}30)")

    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    wb.save(output_path)
    print(f"SUCCESS: {output_path}")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python3 export_csdl_excel.py <payload.json>")
        sys.exit(1)
    generate_excel(sys.argv[1])

