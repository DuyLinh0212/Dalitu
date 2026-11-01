# Account Web: kiểm tra ban đầu

Test được chia theo ứng dụng:

| File | Phạm vi |
| --- | --- |
| `user_account.py` | Các case `U-ACCOUNT-*`: đọc, Hủy, mobile, Lưu, validation và khôi phục hồ sơ User |
| `admin_account.py` | Các case `A-ACCOUNT-*`: đọc hồ sơ Admin, reload, mobile |
| `../support/web_session.py` | Đăng nhập tiền điều kiện, session, screenshot và báo cáo dùng chung |
| `web_account.py` | Launcher tương thích lệnh cũ; không chứa case test |

Hai suite chạy độc lập với `auth.py`, sử dụng các dịch vụ đang chạy;
không build, migrate hoặc seed tài khoản. Chỉ User có tùy chọn ghi và khôi phục
hồ sơ `--write-profile`; Admin chỉ đọc. Đăng nhập là tiền điều
kiện, không được tính là coverage của suite auth.

Mặc định User `http://localhost:4200`, Admin `http://localhost:4201`.
Dùng `--user-url` và `--admin-url` nếu cổng khác; `--application user|admin|all`
chọn ứng dụng cần kiểm tra.

Truyền JSON qua biến môi trường `WEB_E2E_CREDENTIALS` của process:

```json
{"user":{"email":"USER_EMAIL","password":"USER_PASSWORD"},"admin":{"email":"ADMIN_EMAIL","password":"ADMIN_PASSWORD"}}
```

Không commit thông tin đăng nhập; xóa biến môi trường sau khi chạy. Chỉ cần
credential của ứng dụng đã chọn. Script không lưu token, mật khẩu, cookie hay
response body vào report. Screenshot có thể chứa email/tên hiển thị.

Từ root repository, sau khi cung cấp biến môi trường:

```powershell
# User: đọc, Hủy, mobile
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/tests/user_account.py

# User: thêm Lưu, validation và khôi phục
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/tests/user_account.py --write-profile

# Admin
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/tests/admin_account.py

# Chạy cả hai, mỗi suite xuất một report riêng
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/tests/web_account.py --write-profile
```

## Phạm vi hiện tại

- User: đọc tên hồ sơ, mở biểu mẫu, kiểm tra giới hạn bio khai báo 160 ký tự;
  nhập tên/bio tạm rồi Hủy và reload kiểm tra tên gốc; kiểm tra mobile và overflow.
- Admin: xác nhận quyền Admin từ response đăng nhập, đọc hồ sơ, reload,
  kiểm tra mobile và overflow.
- Mặc định không bấm Lưu hồ sơ; không đổi mật khẩu, revoke session hoặc thao tác quản trị dữ liệu.
- Khi bật `--write-profile`: kiểm tra tên trống/121 ký tự bị API từ chối (400),
  textarea bio chặn ký tự thứ 161; lưu tên/bio với khoảng trắng, kiểm tra trim,
  reload và đọc lại; cuối cùng khôi phục hồ sơ ban đầu bằng UI và đối chiếu GET.
  Nếu khôi phục UI lỗi, thử khôi phục qua API và vẫn giữ case UI là FAIL.
  Bản dự phòng `profile-recovery.json` nằm trong run bị Git bỏ qua, chỉ giữ khi
  chưa khôi phục được. Không chứa password/token. Không chạy đồng thời nhiều
  lần với cùng tài khoản. URL API local không chứng minh database cũng là local;
  kiểm tra cấu hình database trước khi bật thao tác ghi.
- Chưa kiểm tra đầy đủ validation phía API, các module Web khác,
  Excel exporter hoặc tích hợp CI. Đây là bộ kiểm tra ban đầu của issue #34.

Kết quả tại `artifacts/runs/<run-id>/report.json`, screenshot tại
`screenshots/<ngày-run>/Account/<user|admin>/<case-id>/<status>.png`.
Report ghi `suite=user-account` hoặc `suite=admin-account`; các case User và
Admin không gộp chung trong một report.
Tiền điều kiện đăng nhập thất bại được ghi riêng trong `setup`; phần kiểm tra
Account bị chặn có trạng thái `blocked`. Exit code khác 0 khi fail hoặc blocked.
Không chạy thử mật khẩu lặp lại khi login bị từ chối.
