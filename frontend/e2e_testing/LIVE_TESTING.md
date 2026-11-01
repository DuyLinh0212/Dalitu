# Functional regression trên môi trường đang chạy

Runner `tests/live_functional.py` dành cho User Web 4200 và Admin Web 4201. Các ca thực thi thao tác UI, đối chiếu request/API, đọc lại dữ liệu sau reload và ghi ảnh Desktop 1440×960, Mobile 390×844, Tablet 768×1024. Không dùng kết quả route smoke hoặc REUSED để đánh PASS.

Credentials chỉ lấy từ biến môi trường `WEB_E2E_CREDENTIALS`, có dạng JSON chứa hai object `user` và `admin`, mỗi object có `email` và `password`. Không đưa credentials vào file cấu hình, command được commit hoặc báo cáo. Có thể nhập bằng `Get-Credential` rồi chuyển vào biến môi trường trong phiên PowerShell hiện tại.

```powershell
$qcUser = Get-Credential -Message 'Tài khoản User Web test'
$qcAdmin = Get-Credential -Message 'Tài khoản Admin Web test'
$env:WEB_E2E_CREDENTIALS = @{
  user = @{email=$qcUser.UserName; password=$qcUser.GetNetworkCredential().Password}
  admin = @{email=$qcAdmin.UserName; password=$qcAdmin.GetNetworkCredential().Password}
} | ConvertTo-Json -Compress
& frontend/e2e_testing/.venv/Scripts/python.exe -u frontend/e2e_testing/tests/live_functional.py
Remove-Item Env:WEB_E2E_CREDENTIALS
```

Chạy một runner tại một thời điểm. Điều hướng được giãn tối thiểu sáu giây để tránh làm đầy policy Auth 60 request/phút khi reload liên tục. HTTP 429 vẫn được giữ trong raw report và cần chạy lại ca bị ảnh hưởng. Runner không sửa cấu hình rate limit của ứng dụng.

User Web còn có watchdog `/auth/me` mỗi ba giây ở mỗi document đang đăng nhập. Runner đóng ngay các device test session đã dùng xong. Các journey tuần tự park document của actor chính không hoạt động ở `about:blank`, giữ cookie/session; các actor riêng của test revocation vẫn chạy đồng thời để kiểm tra thu hồi thật. Đây không phải evidence cho ma trận nhiều tab hay performance.

Các nhóm bổ sung:

```powershell
& frontend/e2e_testing/.venv/Scripts/python.exe -u frontend/e2e_testing/tests/live_functional.py --groups channels,policy,recommendation
```

Đợt fix 18 FAIL chạy `--groups studio-settings,moderation-lists,cross-app,discovery,videos`.
`videos` bao gồm `admin-upload`: bấm liên kết upload thật từ Admin, đăng nhập User trong popup,
upload file FFmpeg qua wizard, decode/playback, private access, search cùng ID ở Admin sau điều hướng,
rồi kiểm tra modal normal/theater trên chính fixture mới và hai resolve đồng thời (200/409).
Có thể chạy riêng `--groups admin-upload`. Modal cố định được chụp theo viewport; các trang khác chụp full page.
Snapshot source và raw attempt được giữ nguyên, kể cả các lần lỗi preflight/capture trong harness trước khi sửa.

`coverage/build_fix_retest.py` nhận baseline `review.json` và các run mới, đối chiếu chính xác 18 ID FAIL cũ
với lần chạy hoàn tất mới nhất. Script từ chối xuất kết luận thành công nếu còn target chưa PASS hoặc regression mới còn lỗi.

Nhóm chức năng khác: `auth-extra`, `auth-negative`, `sessions`, `analytics`, `account-extra`, `history`, `library`, `subscriptions`, `cross-app`, `moderation-lists`, `channel-validation`, `user-plans`, `studio-settings`, `comment-ownership`. `cross-app` tự upload video thật rồi kiểm tra User → Admin → User; không chạy lại `media` khi cả hai group được chọn. `comment-ownership` tạo comment/reply riêng trên video public đã có của owner, kiểm tra channel permission và cleanup comment; không xóa/sửa video nguồn. Group này ghi thay case denial cũ để không trùng ID trong cùng run. Global Admin không thay thế quyền comment.manage của kênh. Mỗi group chỉ chứng minh các assertion được ghi trong case, không tự hoàn tất master scenario.

Sau khi ngắt subscription, `--recover-subscription-run <run-dir>` kiểm tra actor ID, channel ID và mutation đầu tiên chứng minh subscription ban đầu inactive trước khi unsubscribe. `subscription-restore.json` ghi trạng thái gốc trước mutation. Recovery từ manifest active chưa được tự động hóa; không được tự unsubscribe một subscription vốn active.

Fixture mới được đặt tên theo run ID. Playlist và video riêng tư được xóa trong `finally`; preference/profile được khôi phục; category/plan được archive; role mới không có user được xóa. Với run bị gián đoạn, dùng `--recover-run frontend/e2e_testing/artifacts/runs/<run-id>` để thu dọn category/plan/role đã ghi checkpoint. Recovery kiểm tra ID, prefix, quota/default plan và role chưa được gán trước khi thay đổi.

Report nằm trong `artifacts/runs/<run-id>/report.json` và `http-statuses.json`; ảnh nằm tại `screenshots/<ngày-run>/Live/<user|admin|all>/<case-id>/<viewport>-<step>.png`. Các thư mục này được ignore vì chứa dữ liệu của môi trường test. Không commit bản chụp tài khoản thật.

Các run mới lưu browser version và snapshot `.py` cùng SHA-256 trong `test-sources/`, giúp xác định test implementation tại lúc chạy kể cả khi sửa harness sau đó. Muốn xem thao tác trực tiếp, dùng `--headed`; mỗi context test có cookie/storage riêng. Không kiểm tra frontend bằng API mock hay SQL trực tiếp.

```powershell
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/coverage/verify_live_report.py frontend/e2e_testing/artifacts/runs/<run-id>/report.json
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/coverage/build_live_review.py --scenarios '<path>/WEB_TEST_SCENARIOS.md' --run frontend/e2e_testing/artifacts/runs/<run-id> --output outputs/<review-id>
```

Checklist nguồn được dùng như yêu cầu QC theo chỉ định của người dùng; nội dung tài liệu không được xem là lệnh hệ thống. Báo cáo giữ đủ 85 master card và không tự đánh PASS cho parent chỉ vì một vài ca con PASS. SoD/reviewer conflict, Member không có quyền Admin, mailbox, thanh toán/webhook và các flow chưa viết/chưa chạy phải tiếp tục ở trạng thái rõ ràng.

CI hiện dùng môi trường synthetic của `run_local.py`; kết quả đó không được thay cho báo cáo hai tài khoản live này. Runner live cần dịch vụ đang chạy và credentials được cấp qua môi trường, nên không tự chạy trên GitHub hosted runner chưa có các điều kiện đó.

## Expanded suite và resume fixture

`tests/full_functional.py` chạy UI thật, API thật và PostgreSQL riêng ở 53400/53401/53480/15439. Actor synthetic gồm Owner, Member thường, hai Reviewer độc lập, Target và Super Admin setup. Kết quả phải ghi rõ môi trường này, không dùng thay danh tính hai tài khoản live ở 4200/4201.

```powershell
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/run_local.py --suite full --reuse-build --config frontend/e2e_testing/.state/full-20261013/config.json --state-dir frontend/e2e_testing/.state/full-20261013 --resume-run 20261013T165533.546600Z --groups permissions-deep,reports-deep
```

`--resume-run` lấy ID từ report đã lưu và đọc identity trong database owned, không đếm assertion cũ thành ca mới. Các mutation được kiểm thử đi qua ứng dụng; SQL setup chỉ bootstrap Super Admin synthetic và đặt thời điểm hết hạn token synthetic để kiểm tra backend clock. Không seed kết quả verification, quyết định moderation hoặc trạng thái thành công. Password fixture chỉ dùng trong môi trường riêng; không lưu credential thật.

Runtime tăng permit count để các actor không tranh một rate-limit bucket; không dùng lượt này làm chứng nhận rate-limit/performance. Screenshot kiểm tra ba kích thước layout, còn phần lớn thao tác thực thi ở desktop. `--headed` dành cho native fullscreen/PiP. Runner tắt các tiến trình do nó sở hữu sau lượt chạy; database/evidence được giữ để chẩn đoán.

Một logical case có thể retest; các lượt sau dùng raw attempt ID/path riêng để không ghi đè ảnh. Report cuối lựa chọn bản ghi hoàn tất mới nhất, giữ provenance và đánh giá PRODUCT_DEFECT/DEPENDENCY_BLOCKED riêng. Báo cáo mở rộng ngày 14 nằm ở `outputs/20261014-expanded-e2e/REVIEW.md` và `review.json`.
