# Hướng dẫn hoàn thành issue #34 — Playwright Web HuTube

Đối chiếu ngày 07/10/2026, Asia/Saigon. Hướng dẫn chi tiết dưới đây mô tả toàn bộ phạm vi issue. Suite Web đã được triển khai theo từng đợt; xem [lệnh chạy hiện tại](../frontend/e2e_testing/tests/WEB_SUITE.md) và [tiến độ thực thi](WEB_E2E_PROGRESS.md) để biết chính xác case đã chạy và phần còn lại. CF Seeder và simulation được loại khỏi phạm vi theo yêu cầu người dùng. Chưa chạy GitHub Actions cho các thay đổi local này.

Issue: [Viết Test e2e playwright cho Web #34](https://github.com/DuyLinh0212/HuTube/issues/34).

## 1. Sản phẩm cần bàn giao

Issue yêu cầu tiếp tục phần xác thực và CI/CD mẫu, viết E2E cho Web và xuất Excel tổng hợp cùng screenshot qua CI. Có ba đầu ra:

1. Test Playwright cho Web User, Web Admin và các flow xuyên hai ứng dụng theo `WEB_TEST_SCENARIOS.md`, cập nhật theo source mới.
2. Workbook `HuTube_Web_Testcase.xlsx`, giữ cách trình bày của mẫu DevLearningHub nhưng nội dung là case và kết quả thực của HuTube.
3. GitHub Actions artifact chứa workbook, JSON kết quả và screenshot của lần chạy tương ứng; vẫn xuất bằng chứng khi test lỗi.

File Excel mẫu có 16 sheet, gồm Summary, các module API, All Test Cases và E2E Testcases. Sheet E2E có 10 cột: Test Case#, Test Title / Scenario, Test Summary, Test Steps, Test Data, Expected Result, Post-condition, Actual Result, Status, Notes. Các dòng mẫu smoke/mock và chữ Pass thuộc dự án DevLearningHub; chỉ dùng bố cục, không chuyển chúng thành kết quả HuTube.

Tài liệu tải về và `docs/WEB_TEST_SCENARIOS.md` có cùng nội dung theo dòng; khác bytes do cách xuống dòng. Cả hai phản ánh thời điểm cũ, cần đối chiếu source ngày nay.

## 2. Nền tảng và triển khai hiện tại

| Thành phần | Hiện trạng | Phần cần đối chiếu tiếp |
| --- | --- | --- |
| Framework | Python Playwright sync API, `playwright==1.63.0` | Chromium trên GitHub Actions chưa chạy |
| Runner | `run_local.py --suite web`; build .NET/Angular, PostgreSQL riêng, migrate, start/stop server | Không dùng `--reuse-build` sau khi đổi source ứng dụng |
| Test thực thi | `tests/web_all.py` gọi các module User/Admin/cross-app; Auth giữ riêng | Các nhánh còn lại trong WEB_E2E_PROGRESS.md |
| Account/checkpoint | `support/web_suite.py`, fixtures synthetic cố định; dữ liệu pagination riêng | REUSED chỉ kiểm tra dữ liệu tồn tại, không cộng vào thao tác tạo mới |
| Screenshot | `screenshots/<ngày-run>/<nhóm>/<user|admin|all>/<case>/<step>.png` | Verifier kiểm tra PNG và report, không chứng minh full coverage |
| Kết quả | report tổng và User/Admin riêng; Excel 10 cột theo mẫu | Workbook chỉ phản ánh case đã triển khai |
| CI | Thêm Web, exporter openpyxl, verifier và upload XLSX/CSV/PNG | Workflow đã chỉnh local; chưa push/chạy GitHub Actions |
| Inventory | 75 route declarations; bindings/navigation/controls và OpenAPI runtime đã regenerate | Catalog lexical vẫn cần reconcile assertion/branch thủ công |

75 là số khai báo lexical, gồm redirect/alias/conditional/duplicate; không phải 75 màn hình độc lập.

`artifacts/auth-results.md` ghi các lần chạy lịch sử ngày 02/10: warm run 11 PASS + 3 REUSED. Đây không phải kết quả đã kiểm chứng lại trên máy này. Checkout hiện không có `artifacts/latest.json` và không tìm thấy PNG trong `screenshots/baseline-auth`; không dùng những link lịch sử như bằng chứng file đã có tại local.

Đã chạy kiểm tra tĩnh `coverage/verify_catalog.py` tại checkout này: thất bại với `AssertionError: Missing method in appendix`. Hai method trong inventory chưa có ở phụ lục là `PaymentIdempotency_IsScopedToUserAndSurvivesCompletionAndConcurrency` và `AutoLockRule_FifthVideoRejection_LocksChannelAndCreatesStrike`. Cần đồng bộ phụ lục backend test. Sau khi xử lý lỗi này, validator còn phụ thuộc baseline/latest và các điều kiện auth cố định.

## 3. Bản đồ source để viết đúng test

Một case phải lần theo chuỗi: route → template thao tác → method component → Angular service → controller → service nghiệp vụ/persistence. Kết quả lưu thành công cần chứng minh bằng reload/GET/DB, không chỉ toast.

Các đường dẫn trong bảng dưới đây tính từ root repository.

| Nhóm | UI cần đọc | API và nghiệp vụ cần đối chiếu | Assertion trọng tâm |
| --- | --- | --- | --- |
| AUTH/AUTH-EXT | Hai `features/auth`, `core/auth.service.ts`, `auth.interceptor.ts` | `AuthController.cs`, `Application/Auth/AuthService.cs` | UI login/logout/verify/reset, session và cookie theo platform |
| U-ACCOUNT | User `features/account/account-page.ts/.html`, `core/account.service.ts` | `AccountController.cs`, `Infrastructure/Account/AccountService.cs` | PATCH profile; PUT settings/notifications; đổi password và revoke session |
| U-CHANNEL | User `features/channel`, `core/channel.service.ts` | `ChannelController.cs`, `Application/Channels/ChannelService.cs` | Owner/member, handle, branding, lời mời, subscribe và persist |
| U-STUDIO | User `features/studio/pages`, `features/studio/upload`, `core/upload-state.service.ts` | Videos trong `ContentControllers.cs`, `ContentService.cs`, `VideoRenditionProcessing.cs`, `FfmpegVideoTranscoder.cs` | File thật, preflight, idempotency, probe/transcode và trạng thái xử lý |
| U-WATCH/U-LIBRARY | User `features/video/watch-page`, `features/library`, `core/content.service.ts` | Video/Comments/Library trong `ContentControllers.cs`, `VideoAccessPolicy.cs` | Playback thật, progress, reaction/comment/download và quyền truy cập |
| U-DISCOVERY/U-SUBSCRIPTION | User `features/home`, `explore`, `subscriptions` | Feed trong `ContentControllers.cs`, `SubscriptionsController.cs` | Filter/query/count/page và giới hạn nội dung có thể xem |
| U-PLAYLIST | User `features/playlists`, `core/playlist.service.ts` | `PlaylistController.cs`, `Infrastructure/Playlists/PlaylistService.cs` | Visibility, thứ tự item, ownership, cover và lưu lại |
| U-POLICY | User `features/policy` | `PolicyController.cs`, `Infrastructure/Policies/PolicyService.cs` | Nội dung/version công bố |
| U-PLAN | User `features/plans`, `core/plan.service.ts` | `PlansController.cs`, `PaymentsController.cs`, `PlanService.cs`, `PaymentService.cs` | Sandbox payment, entitlement đúng một lần, allocation và invite |
| A-USERS/A-CHANNELS/A-VIDEOS | Admin `features/users`, `channels`, `videos` và service từng nhóm | `AdminController.cs`, `AdminUserService.cs`, `AdminContentService.cs` | Filter/page/export, reason/audit, lock/status tác động phía User |
| A-RBAC | Admin `features/rbac`, `core/permission.guard.ts` | `AdminController.cs`, `Application/Rbac/RbacService.cs`, `Authorization/PermissionAuthorization.cs` | Từng permission allow/deny, direct URL và API |
| A-PLANS/A-TOPICS/A-POLICY | Admin `features/plans`, `topics`, `policies` | `AdminController.cs`, `PlanService.cs`, `TaxonomyService.cs`, `PolicyService.cs` | Create/edit/archive/version và persist |
| A-MODERATION/X-MODERATION | Admin `features/moderation`, User creator/report/appeal UI | `AdminController.cs`, `UserModerationController.cs`, `ModerationService.cs`, `ReportService.cs`, `AppealService.cs`, `StrikeService.cs` | Claim conflict, quyết định đúng target, reviewer khác xử lý appeal, hiệu lực phía User |
| A-CF/A-RECOMMENDATION | Admin `features/cf-seeder`, `recommendations`, `core/super-admin.guard.ts` | `CfSeederController.cs`, `RecommendationAdminController.cs`, recommendation services và Python service | Job ID, poll trạng thái, cancel, thiếu dependency rõ ràng |
| Admin mới | `features/payments`, `subscriptions`, `operations` | `AdminPaymentsController.cs`, `AdminOperationsController.cs`, subscriptions trong `AdminController.cs` | Payment filters/detail/export; extend/change-plan; reports/logs/system signals |
| X-SHELL/X-UI/X-ACCESS | Shell/header/sidebar/components, i18n/theme, route guards hai app | Notifications/SignalR và auth/permission backend | Keyboard, responsive, loading/error, reconnect và không leak dữ liệu |

### Những khác biệt cụ thể cần đưa vào phạm vi

- Admin `/`, wildcard và `/dashboard` hiện redirect `/roles`, trong khi tài liệu cũ nói `/account`. Login thành công vẫn được auth mẫu kiểm tra về `/account`; hai hành vi này cần case riêng.
- Admin hiện có `/forbidden`, `/subscriptions`, `/payments`, `/system/notifications`, `/system/reports`, `/system/logs`. Thêm các nhóm A-SUBSCRIPTIONS, A-PAYMENTS, A-OPERATIONS vào scenario và mapping inventory.
- `generate_inventory.py` chưa có mapping cho các feature mới này, nên bindings có thể rơi vào X-SHELL. API mapping chưa liệt kê AdminPayments/AdminOperations; đối chiếu tag OpenAPI thực tế để bổ sung, tránh `Unmapped API tag`.
- User upload có thêm video cards và promotion; playlist có cover. Đọc enum/validation hiện hành và thêm case cho UI mới ngoài tài liệu gốc.
- `submitAddVideo()` của Admin, `admin-videos-page.ts:461`, chỉ đặt notice và đóng modal. Không có lời gọi API tạo video. Nếu case yêu cầu tạo thật, ghi FAIL với bằng chứng không persist và liên kết bug; không đổi assertion thành chỉ kiểm tra toast.
- Một số lựa chọn account ghi `localStorage` như download quality/smart downloads/top fan/super chat. Ghi đúng contract đang có: reload cùng browser giữ được không chứng minh persist database hoặc đồng bộ máy khác.
- `Storage__Provider=Local` trong runner chưa đủ mô tả cách chọn storage: `Program.cs` chọn Cloudinary/R2 theo credentials. Full suite cần cấu hình credentials trống và kiểm tra DI/local media, tránh dùng nhầm tài nguyên external.
- `LocalStorageService` lưu dưới `HuTube.Api/uploads` và `.private_uploads`. Khi chạy media nhiều profile, thiết kế cách cô lập file storage hoặc lưu manifest; không giả định toàn bộ file nằm trong `.state`.

## 4. Khởi động baseline auth trên máy bạn

Kiểm tra tại máy này: .NET SDK 10.0.401, Node 22.22.3, Python mặc định 3.11; bundled Python của Codex là 3.12.14. PostgreSQL tìm thấy tại `C:\Program Files\PostgreSQL\17\bin`; đường dẫn `D:\PostgreSQL\18\bin` trong mẫu không tồn tại. Chưa có `.venv` E2E. Chrome đã có.

Nên cài PostgreSQL 18 để đồng nhất CI trước khi nghiệm thu. Nếu dùng PostgreSQL 17 cho khảo sát local, ghi rõ khác version và chạy migration để xác nhận tương thích; chưa coi là tương đương CI 18.

PowerShell từ root HuTube, sau khi Python 3.12+ và PostgreSQL 18 đã sẵn sàng:

```powershell
python --version
dotnet --list-sdks
node --version
# Nếu python mặc định còn 3.11, dùng py -3.12 hoặc đường dẫn Python 3.12+ thực tế.
py -3.12 -m venv frontend/e2e_testing/.venv
& frontend/e2e_testing/.venv/Scripts/python.exe -m pip install -r frontend/e2e_testing/requirements.txt
& frontend/e2e_testing/.venv/Scripts/python.exe -m playwright install chromium
npm --prefix frontend/user-web ci
npm --prefix frontend/admin-web ci
# Chỉnh đường dẫn này theo nơi cài PostgreSQL 18 thực tế.
$env:E2E_POSTGRES_BIN='C:\Program Files\PostgreSQL\18\bin'
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/run_local.py --help
& frontend/e2e_testing/.venv/Scripts/python.exe frontend/e2e_testing/run_local.py
```

Nếu muốn dùng Chrome có sẵn, đặt `CHROME_BIN` theo README. `py -3.12` ở trên là lựa chọn nếu máy có Python Launcher và bản Python đó, không phải phần mềm đã được xác nhận có trên máy.

`config.example.json` hiện đặt PostgreSQL port **15439**, khác README/scenario cũ **55439**. User/Admin/API vẫn là 53400/53401/53480. Lấy config runner làm giá trị thực tế; kiểm tra cổng trước chạy. DB vẫn là `hutube_e2e_runner`, tách khỏi DB ứng dụng `hutube`.

Sau run:

```powershell
$latest = Get-Content frontend/e2e_testing/artifacts/latest.json -Raw | ConvertFrom-Json
$report = Get-Content (Join-Path $latest.run_dir 'report.json') -Raw | ConvertFrom-Json
$report | Select-Object passed,reused,failed,account_count,created_account_count,duration_ms
Get-ChildItem -LiteralPath $report.screenshot_dir -Recurse -File
```

Warm run dùng `--reuse-build` chỉ khi source ứng dụng chưa đổi. Giữ `.state` để dùng lại account và checkpoint. Không xóa DB để chữa test lỗi. Hiện auth cuối suite logout và khôi phục role; flow sau cần login lại đúng actor, không giả định session auth còn sống.

## 5. Sửa harness trước khi tăng số test

Thực hiện một đợt nhỏ, giữ 14 case auth làm regression:

1. Tách `execute`, `shot`, `observe`, `go`, login helpers trong auth thành support dùng chung. Giữ `auth_scope` tương thích, thêm metadata explicit cho app/module/flow.
2. Thêm entrypoint `tests/run_suite.py` và tham số runner `--suite auth|core|media|admin|full`. Đây là đề xuất cần code thêm, **hiện chưa có**. Ghi rõ prerequisite của từng lựa chọn.
3. Thêm registry case cố định: ID, module, actor, precondition, steps, data, expected, postcondition, dependency IDs. Report join với registry bằng ID.
4. Ghi kết quả theo từng case và checkpoint ngay sau mutation có tác dụng; nên persist report tăng dần để còn dữ liệu khi process bị timeout/kill.
5. Lỗi một case phải ghi FAIL, screenshot và trace. Case phụ thuộc ghi BLOCKED kèm ID/lý do; case độc lập tiếp tục nếu môi trường vẫn khỏe. Auth hiện re-raise lỗi và dừng chuỗi, chưa phải full-suite scheduler.
6. Tách lỗi startup/build/migration thành lỗi môi trường, không gán PASS hay bịa observed cho case chưa chạy. Luôn xuất manifest run dù chưa mở được browser.
7. Tách kiểm tra catalog tĩnh khỏi validator kết quả auth. Bỏ giả định full report có đúng 14 case/account_count=1 khi thêm actor. Kiểm tra đúng registry/suite, thiếu bằng chứng và case ID trùng.
8. Cập nhật docs/mapping/inventory cho source mới, bao gồm phương thức backend bị thiếu trong phụ lục. Validator hiện không phải thước đo toàn bộ coverage đã đạt.

Không chỉ thêm file `account.py` rồi chạy runner cũ: runner cũ chỉ thực thi `auth.py`. Không dùng lệnh `npx playwright test` cho bộ Python hiện tại. Karma `npm test` trong job web là lớp kiểm thử khác.

### Actor và dữ liệu

Giữ owner hiện có, bổ sung member, reviewer1, reviewer2, superadmin theo email cố định trong scenario. Reviewer phải được gán role/permission thực tồn tại, không giả định database có role code `reviewer`. Nếu chưa có, provision role test riêng, lưu ID và permissions trong manifest.

Dùng context riêng cho từng actor và app; hai app có refresh cookie khác tên nhưng cùng backend. Access token của Angular ở memory, refresh cookie HttpOnly. Chỉ lưu `storage_state` rồi mở context mới chưa bảo đảm mọi API request đã có Bearer token.

`context.request.get()` không tự lấy access token từ Angular signal và không đi qua interceptor. Để kiểm chứng dữ liệu, bắt GET do UI gọi lúc reload, hoặc tạo API helper với token test chỉ giữ trong memory; không đưa token vào report. Xem [Playwright authentication](https://playwright.dev/python/docs/auth).

Resource checkpoint cần có owner/purpose, channel/video/playlist/comment/payment/report/case/appeal/job IDs, file hash/duration. Kiểm tra tồn tại trước create; retry update/poll resource cũ. Dùng resource phụ để test delete/purge. State chia sẻ phải chạy tuần tự; chỉ song song nếu DB/account/storage được cô lập.

## 6. Test đầu tiên nên viết: lưu bio và reload

Mục tiêu đầu tiên sau khi baseline chạy: U-ACCOUNT.01-save-bio, rồi cancel/validation/avatar. Case này nhỏ, không cần FFmpeg/payment/recommender, và kiểm tra đầy đủ UI → API → persistence.

Source xác nhận:

- Nút mở form là `.yt-action-link-btn.primary-action` trong `app-account-page`.
- Field `#dn` và `#bio`; bio maxlength 160.
- Nút save trong `.profile-inline-form` có class `.btn-yt-primary`, gọi `onSaveProfile()`.
- API là `PATCH /api/v1/account/profile`; reload gọi `GET` cùng path.

Ví dụ function để ghép vào suite **sau khi login owner bằng UI**. `capture` là callback của reporter mới, không phải helper đã tồn tại. Giữ displayName cũ để auth regression còn dùng tên trong checkpoint. Đoạn minh họa này chưa chạy browser trong lần đọc source này.

```python
from playwright.sync_api import expect

def save_bio_and_reload(page, config, user_id, capture):
    bio = 'Tìm hiểu về lao động, học tập và sáng tạo.'
    profile_path = '/api/v1/account/profile'

    def profile_response(response, method):
        return (response.request.method == method
                and response.url == config['api_url'] + '/account/profile')

    with page.expect_response(lambda r: profile_response(r, 'GET')) as initial:
        page.goto(config['user_url'] + '/account')
    assert initial.value.status == 200
    before = initial.value.json()
    assert before['userId'] == user_id

    account = page.locator('app-account-page')
    account.locator('.yt-action-link-btn.primary-action').click()
    expect(account.locator('#dn')).to_have_value(before['displayName'])
    account.locator('#bio').fill(bio)

    with page.expect_response(lambda r: profile_response(r, 'PATCH')) as saved:
        account.locator('.profile-inline-form .btn-yt-primary').click()
    assert saved.value.status == 200
    actual = saved.value.json()
    assert actual['userId'] == user_id
    assert actual['displayName'] == before['displayName']
    assert actual['bio'] == bio
    expect(account.get_by_role('status')).to_contain_text('Đã lưu thông tin hồ sơ')
    capture(page, '01-saved-desktop')

    with page.expect_response(lambda r: profile_response(r, 'GET')) as loaded:
        page.reload()
    assert loaded.value.status == 200
    assert loaded.value.json()['bio'] == bio
    account.locator('.yt-action-link-btn.primary-action').click()
    expect(account.locator('#bio')).to_have_value(bio)
    capture(page, '02-reloaded-desktop')
    return {'observed': 'PATCH 200; GET sau reload giữ đúng bio và owner',
            'api_statuses': [{'method': 'PATCH', 'path': profile_path, 'status': 200},
                             {'method': 'GET', 'path': profile_path, 'status': 200}],
            'resource_ids': {'user_id': user_id}}
```

Bắt response trước click/goto để tránh bỏ lỡ response nhanh; cách dùng được mô tả trong [Playwright Page.expect_response](https://playwright.dev/python/docs/api/class-page#page-expect-response). Khi tích hợp đặt language vi, timeout của `expect` và page theo config; có thể bổ sung data-testid nếu selector hiện tại thiếu ổn định.

Tách case cancel: sửa bio, Hủy, reload, GET vẫn bằng giá trị trước sửa. Kiểm tra mở form lại và local field có reset đúng hay không; không suy luận cancel đã đúng chỉ vì form biến mất. Với save dùng cùng giá trị trên warm run, thêm case đổi qua lại giữa hai bio cố định để có assertion mutation mới, không tạo dữ liệu random.

## 7. Lộ trình triển khai toàn bộ

| Đợt | Test cần viết | Điều kiện hoàn tất đợt |
| --- | --- | --- |
| 1 | Harness/report/Excel, giữ AUTH, thêm AUTH-EXT và U-ACCOUNT | Fresh/warm run đúng; một case save/cancel có GET sau reload; artifact có Excel |
| 2 | U-CHANNEL, U-PLAYLIST cơ bản, policy/catalog, guest guards | Tạo/dùng lại tài nguyên, member permission, ID checkpoint và responsive |
| 3 | U-STUDIO và A-MODERATION approve đầu tiên → U-WATCH | Upload thật một lần, probe/transcode, approve khi cần, playback thật |
| 4 | Discovery/search/subscriptions/history/liked, playlist item/reorder, comment/reply/reaction/download | Dataset nhiều trang, dữ liệu đúng actor, persist và authorization |
| 5 | Admin account/RBAC/users/channels/videos/topics/policies/plans | Case đủ/thiếu từng quyền; audit; mutation có tác dụng phía User |
| 6 | Reports/appeals/strikes + cross-app lock/revoke/membership | Context actor riêng; cùng resource ID; reviewer không tự xử appeal |
| 7 | Plan/payment sandbox, Admin payments/subscriptions/operations | Payment idempotent, entitlement, filters/export, extend/switch-plan/logs |
| 8 | CF/recommendation/simulation, nhánh lỗi/responsive/performance | Dependency thật hoặc BLOCKED rõ; job polling; đo performance riêng |

Mỗi nhóm áp dụng X-ROUTES/X-FORM/X-ACCESS/X-UI/X-ERROR/X-DATA/X-STATE. Test các lựa chọn enum thực tế trong UI/contract. Mỗi binding cần case/step/assertion tương ứng; một case có thể chứng minh nhiều binding nếu mapping cụ thể. Không biến số bindings thành số test hay phần trăm coverage tự động.

Các profile môi trường nên tách:

- Auth/core: API thật + DB riêng + Email Pickup; không cần recommendation. Account/channel có thể chạy trên nền auth hiện tại sau khi mở rộng dispatcher.
- Media/moderation: bật `VideoProcessing__Enabled`, cấu hình `VideoProcessing__FfmpegPath`/`FfprobePath`, xác định rõ `Features__ModerationEnabled` và `Features__PlanEnforcementEnabled`. Không thể chỉ export env rồi chạy runner cũ vì runner gán cứng video processing false.
- Recommendation/payment: sandbox và service/credential test chuyên dụng; không dùng `.env.local` production/dev thật làm ngầm định. Dependency thiếu ghi BLOCKED theo case.

Với moderation bật, upload public không đương nhiên xem được ngay. Đối chiếu trạng thái processing và moderation; reviewer approve rồi mới kiểm tra publish/watch theo contract. Quota gói free có thể làm upload 99 MB bị từ chối; cấp entitlement fixture hợp lệ trước happy path, còn case quota phải xác nhận từ chối đúng.

File video trong tài liệu thuộc ổ F của tác giả cũ; đổi `video_path` sang file thật trên máy, kiểm tra hash/size/duration. CI cần cách cung cấp file qua artifact/download fixture có quyền sử dụng hoặc tạo clip nhỏ xác định bằng FFmpeg. Nếu dùng clip nhỏ thay file gốc, ghi rõ test nào chứng minh upload nhỏ và test 99 MB nào vẫn chưa chạy. Không commit video 99 MB vào Git.

User upload hiện qua multipart `POST /videos`; CF seeder có upload-session/chunk riêng. Không dùng quy tắc chunk 24 MiB của CF cho mọi flow upload.

Action 15s; navigation 45s; API 30s theo mẫu. Media/job cần deadline riêng, poll trạng thái, không chờ networkidle với player/SignalR. Suite 300s và CI 30 phút hiện chỉ phù hợp auth; full suite cần job/budget riêng dựa trên đo thực tế.

## 8. JSON → Excel và screenshot

Nên để JSON là nguồn kết quả thực thi, Excel là bản trình bày có thể sinh lại. Không điền Actual Result = Expected Result hàng loạt.

Registry giữ thiết kế case; reporter giữ `run_id`, commit, suite, actor, start/end/duration, status, observed, error, API method/path/status, resource IDs, screenshots và evidence run gốc cho REUSED. Join registry với report theo ID. Case không được chạy vẫn xuất với PLANNED hoặc BLOCKED và lý do.

Đề xuất workbook:

- Summary: module, total, PASS, FAIL, BLOCKED, PLANNED, REUSED, tỷ lệ PASS trong case thực thi. Định nghĩa mẫu số rõ; không cộng REUSED vào PASS của lần chạy mới.
- E2E Testcases: cùng 10 cột mẫu; precondition nằm trong Test Summary hoặc metadata đầu sheet, Notes chứa đường dẫn ảnh tương đối/run/resource/error.
- Sheet module khi số case lớn, hoặc một bảng All Test Cases để lọc. Không sao chép các sheet API DevLearningHub không liên quan.

Status nội bộ hiện là `passed`, `failed`, `reused`; exporter normalize sang `PASS`, `FAIL`, `REUSED`. Thêm `BLOCKED`, `PLANNED` có định nghĩa. Tỷ lệ PASS trong case thực thi = PASS/(PASS+FAIL); nếu mẫu số 0 hiển thị N/A. Summary phải thể hiện riêng tiến độ toàn phạm vi để tỷ lệ 100% của vài case không gây hiểu nhầm.

Exporter là file mới đề xuất `frontend/e2e_testing/support/export_report.py` hoặc exporter JS tương ứng. Chọn thư viện phù hợp môi trường CI, pin dependency và dùng template HuTube đã review. Runtime GitHub Actions phải tự cài được dependency; không phụ thuộc đường dẫn runtime riêng của Codex trên máy tác giả.

Output đề xuất:

```text
artifacts/runs/<run-id>/
  report.json
  http-statuses.json
  run-manifest.json
  HuTube_Web_Testcase.xlsx
screenshots/<ngày-run>/
  Account/user/U-ACCOUNT.01-save-bio/01-saved-desktop.png
  CrossApp/all/X-MODERATION-WATCH-01/01-approved-desktop.png
```

Lưu ảnh theo ngày-run/nhóm/phạm vi/case; tên file ghi bước chụp và viewport, JSON giữ metadata module/flow. Chụp trước/sau và lỗi/modal khi có ích; mask password và dữ liệu nhạy cảm hiển thị. Capture một viewport không thay thế việc tương tác kiểm thử trên mobile/tablet.

Workbook và screenshot được đưa vào cùng artifact để link tương đối trong Excel tiếp tục trỏ tới đúng ảnh sau khi tải và giải nén. Exporter tính đường dẫn từ thư mục workbook tới cây `screenshots`; không ghi đường dẫn tuyệt đối trên máy chạy.

## 9. Nối vào GitHub Actions

Giữ job e2e mẫu, bổ sung exporter sau run với `if: always()`; exporter đọc manifest/report của đúng lần chạy, không lấy kết quả local cũ. Nếu startup fail và không có report, vẫn xuất các case chưa chạy cùng lỗi môi trường. Không che exit code fail của suite bằng exporter thành công.

Trong bước upload hiện có, thêm đường dẫn sau vào `path`:

```yaml
frontend/e2e_testing/artifacts/runs/*/HuTube_Web_Testcase.xlsx
frontend/e2e_testing/artifacts/runs/*/run-manifest.json
frontend/e2e_testing/artifacts/runs/*/http-statuses.json
```

Đóng gói `report.json`, `screenshots/*/*/*/*/*.png` và ảnh kiểm tra giao diện đã được đưa về cùng cấu trúc vào một artifact, với `if: always()` và retention đã cấu hình. Mỗi job chỉ tạo run của chính nó; nếu dùng cache/build download, không cache thư mục artifacts/runs làm dữ liệu đầu vào báo cáo. Trace/mail/checkpoint không đưa nguyên vào artifact công khai vì chứa token/cookie/password.

Job e2e hiện `needs: [backend, web]`, chạy sau hai nhóm đó. Nếu chúng fail trước thì e2e không chạy và không có screenshot; báo rõ thay vì khẳng định mọi CI thất bại đều có ảnh browser.

Workflow push chỉ match develop và feature/fix/ci/test/db; nhánh `codex/...` không tự match push hiện tại. Có thể dùng PR vào develop hoặc workflow_dispatch, hoặc bổ sung pattern đúng khi cần. Chưa cần bật deploy staging để hoàn thành issue kiểm thử này.

Sau full suite lớn, tách core/media/external jobs theo dependency và dữ liệu riêng; không chia cùng checkpoint owner cho nhiều worker. Chứng minh artifact tải về mở được workbook và ảnh; không chỉ xác nhận upload step màu xanh.

## 10. Checklist nghiệm thu issue

- [ ] Baseline auth chạy fresh và warm trên môi trường đã ghi rõ version/config.
- [ ] Source hiện tại được reconcile với scenario, gồm Admin mới, cover/video cards/promotion và guards.
- [ ] Registry có case ID ổn định, mapping route/binding/enum/permission → assertion và bằng chứng.
- [ ] Core/media/admin/cross-app được dispatcher thực thi, không còn chỉ là README kế hoạch.
- [ ] Actor và resource tái sử dụng có reconciliation; mutation fail không tạo trùng.
- [ ] Placeholder/bug/dependency thiếu ghi FAIL/BLOCKED phù hợp, không chuyển thành PASS để làm đẹp báo cáo.
- [ ] Lỗi độc lập, lỗi prerequisite và timeout môi trường phân biệt; report còn tồn tại sau failure.
- [ ] Excel khớp JSON: ID/status/count/observed và Notes mở đúng ảnh của run.
- [ ] CI chạy đúng suite; artifact có Excel + JSON + screenshots, kể cả run browser thất bại.
- [ ] Case bắt buộc còn BLOCKED/PLANNED được nêu rõ; muốn đóng toàn scope phải xử lý hoặc thống nhất phần loại trừ với chủ issue.
- [ ] README có lệnh chạy thực tế, profile/dependency, recovery checkpoint, cách tải artifact.
- [ ] PR vào develop liên kết issue và ghi đúng các test đã chạy, lỗi còn lại, artifact CI; không suy local PASS thành CI PASS.

Bước bắt đầu cụ thể: chuẩn bị Python/PostgreSQL đúng version → chạy auth mẫu → sửa harness/report/Excel → viết U-ACCOUNT.01-save-bio và cancel → kiểm tra artifact CI. Khi vòng này hoạt động, mở rộng từng đợt trong bảng mục 7.
