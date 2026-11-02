# Web User / Admin E2E

Suite Python Playwright dùng database PostgreSQL E2E riêng, các tài khoản
`@example.test` cố định và ba server do runner quản lý. CRUD không dùng
database `.env.local` hoặc tài khoản cá nhân.

## Chạy từ root repository

```powershell
$env:CHROME_BIN = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$env:E2E_POSTGRES_BIN = 'C:\Program Files\PostgreSQL\18\bin'
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/run_local.py --config frontend/e2e_testing/config.local.json --suite web
```

Sau khi đã build và không đổi source ứng dụng, thêm `--reuse-build`. Không cần bật
server 4200/4201/5080. Runner từ chối port bị chiếm và dừng process do chính nó tạo.
FFmpeg/ffprobe cần trong PATH. CI dùng Chromium qua `python -m playwright install --with-deps chromium`.

## Tách test

| Phạm vi | Script | Assertion |
| --- | --- | --- |
| User Account | `tests/user_account.py`, `flows/user/preferences.py` | Read/cancel/validation/save/restore; notification/privacy persist |
| User channel | `flows/user/functional.py`, `flows/user/membership.py`, `flows/user/channel_settings.py` | Create/public/subscribe; invite/accept/role/remove; metadata/links/contact clear/PNG branding |
| User playlist | `flows/user/functional.py`, `flows/user/playlist_items.py`, `flows/user/playlist_settings.py` | Create/edit/visibility/cancel/delete; picker add/remove |
| User media/watch | `flows/user/media.py`, `flows/user/interactions.py`, `flows/user/ratings.py` | Upload thật/FFmpeg/UI playback; reaction/comment/rating |
| User lỗi | `flows/user/errors.py` | Injection search 500 → retry API thật |
| Admin Account | `tests/admin_account.py` | Identity/reload/mobile |
| Admin users | `flows/admin/functional.py`, `flows/admin/pagination.py`, `flows/admin/user_controls.py` | Search/detail/status/role filters; 55-row pagination; lock/unlock |
| Admin channels | `flows/admin/channels.py` | Search/pageSize/detail; downloaded CSV Unicode/formula escape |
| Admin policies | `flows/admin/policies.py` | Create/publish version; public API sync; User mutation denied |
| Admin taxonomy | `flows/admin/taxonomy.py` | Category create/edit/archive; tag create/delete |
| Admin plans | `flows/admin/plans.py` | Create/edit/archive, bytes/seconds, catalog User |
| Admin RBAC | `flows/admin/rbac.py` | Create/reason; cancel/save permission; deny User |
| Xuyên ứng dụng | `flows/cross_app/moderation.py` | User public → Admin approve → member watch → private |

`tests/web_all.py` điều phối. Case SMOKE chỉ kiểm tra route; REUSED kiểm tra fixture
đã tồn tại, không chạy lại thao tác tạo. Mutation vẫn chạy sau bước reuse.

## Bằng chứng

- `artifacts/web-latest.json`: lần chạy Web mới nhất, độc lập Auth `latest.json`.
- `artifacts/runs/<run-id>/report.json`, `user-report.json`, `admin-report.json`.
- `screenshots/<ngày-run>/Web/<user|admin|all>/<case-id>/<status>.png`.
- `.state/web-fixtures.json`: ID tài nguyên chính, giữ để retry không tạo trùng.

Member tạm, bình luận, playlist item và playlist phụ được dọn sau test; kênh/video/playlist chính
được giữ. Video FFmpeg 640×360, 10 giây là fixture nhỏ; chưa thay file lớn/chunk/720p.

```powershell
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/coverage/verify_web_evidence.py
```

Excel dùng 10 cột của sheet E2E trong mẫu, tách Summary/User/Admin. Local:
`coverage/export_web_report.mjs` dùng bundled artifact-tool và render QA.
CI không có bundled runtime: cài openpyxl 3.1.5 rồi chạy
`python frontend/e2e_testing/coverage/export_ci_report.py`.
CI tạo thêm `artifacts/runs/<run-id>/playwright-report.html` cho lượt Web và RBAC.
Tải artifact `hutube-e2e-evidence-*` trong trang Actions để xem báo cáo HTML cùng
ảnh minh chứng. Báo cáo cho phép lọc ca theo mã/tên, ứng dụng và trạng thái.
CI export/upload JSON/XLSX/HTML/screenshot kể cả khi test fail.

## Chưa hoàn tất toàn bộ scenario

`docs/WEB_TEST_SCENARIOS.md` vẫn là kế hoạch đầy đủ; `docs/WEB_E2E_PROGRESS.md`
ghi kết quả và phần còn lại. CF Seeder và simulation được loại khỏi phạm vi theo
yêu cầu người dùng ngày 07/10/2026. Giữ PLANNED cho các nhánh chưa có assertion:
mọi channel permission; MIME/size boundaries; playlist reorder nhiều item;
discovery/library nhiều trang; Studio nâng cao; mutation Admin videos/channels;
public policy UI (hiện hiển thị rule tĩnh); report/appeal/strike/SoD;
toàn bộ responsive/keyboard/performance. Recommendation và payment sandbox
cần môi trường test riêng. Runner hiện dùng
Local storage, tắt recommendation service, không chuyển tiền thật. Tổng PASS của
suite này chưa chứng minh issue #34 đã bao phủ mọi binding.
