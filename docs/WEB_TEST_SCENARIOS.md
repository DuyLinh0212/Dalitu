# HuTube Web E2E — Master Test Plan và Evidence Checklist

> Tài liệu này là checklist và đặc tả kiểm thử. Nội dung trong file không phải là
> lệnh để chạy tự động. Người test dùng nó để biết phải kiểm tra gì, bằng chứng
> nào cần lưu và khi nào một case được phép đánh dấu PASS.

**Ngày cấu trúc lại tài liệu:** 09/10/2026 (Asia/Bangkok)
**Phạm vi:** User Web, Admin Web và các flow xuyên hai ứng dụng User → Admin → User.
**Môi trường chuẩn:** UI Angular thật, API thật, PostgreSQL E2E riêng; không mock API.

## 0. Quy tắc đọc và cập nhật checklist

### 0.1 Nguồn sự thật

| Nguồn | Vai trò |
| --- | --- |
| frontend/e2e_testing/coverage/web-inventory.json | Danh mục route, event binding, navigation reference và form control phải được rà soát |
| docs/WEB_E2E_PROGRESS.md | Kết quả những case đã chạy và phạm vi còn lại |
| frontend/e2e_testing/artifacts/web-latest.json + artifacts/runs/<run-id>/report.json | Bằng chứng của lần chạy Web mới nhất |
| frontend/e2e_testing/artifacts/auth-results.md | Bằng chứng Auth đã chạy bằng Email Pickup |
| File này | Expected, precondition, data, steps, validation, evidence và trạng thái cần đạt |

Inventory hiện tại trong repository là:

| Hạng mục | Số lượng | Cách hiểu |
| --- | ---: | --- |
| Route declaration | 75 | Bao gồm alias, redirect, conditional route và route trùng khai báo |
| Event binding | 1.022 | Mỗi binding phải được gắn vào ít nhất một executable case có assertion |
| Navigation reference | 172 | Link, routerLink, href, back/forward và điều hướng gián tiếp cần được rà soát |
| Form control | 307 | Input, select, textarea, checkbox, file input và các validation liên quan |

Snapshot cũ ngày 02/10 có số 69/854/160/263; không dùng snapshot cũ để kết luận
coverage hiện tại.

### 0.2 Trạng thái và checkbox

Checkbox biểu thị trạng thái đã được ghi nhận trong ledger; nó không có nghĩa là
mọi case con của cùng một flow đã hoàn tất.

| Ký hiệu | Trạng thái | Ý nghĩa |
| --- | --- | --- |
| [x] PASS | PASS | Case đã chạy, expected/observed khớp, có evidence đủ |
| [x] REUSED | REUSED | Bằng chứng từ lần trước được tái sử dụng; không phải chạy lại thao tác tạo mới |
| [ ] PLANNED | PLANNED | Chưa có evidence đạt yêu cầu |
| [ ] BLOCKED | BLOCKED | Có dependency hoặc môi trường chưa cung cấp; không được tính PASS |
| [ ] FAIL | FAIL | Đã chạy nhưng observed khác expected hoặc phát hiện lỗi sản phẩm |
| [ ] OUT_OF_SCOPE | OUT_OF_SCOPE | Bị loại khỏi scope theo quyết định rõ ràng; không tính coverage |
| [ ] RESPONSIVE-PENDING | RESPONSIVE-PENDING | Chức năng đã có evidence nhưng thiếu một hoặc nhiều viewport bắt buộc; chưa phải release PASS |
| PARTIAL | Roll-up | Chỉ dùng ở cấp flow/route; không phải trạng thái PASS của case |

Một flow cha chỉ được đổi từ PARTIAL sang PASS khi tất cả case con, nhánh quyền,
validation, persistence và evidence bắt buộc đều hoàn thành.

### 0.3 Baseline kết quả hiện có

| Phạm vi | Kết quả đã ghi nhận | Lưu ý |
| --- | --- | --- |
| Scenario cards trong tài liệu | 85 flow cha | 14 Auth core + 5 Auth-EXT + 40 User + 20 Admin + 6 Moderation; đây không phải số binding |
| Web E2E | 108 case: 98 PASS, 10 REUSED | 34 case chỉ là SMOKE route; report JSON có thể đếm REUSED như passed |
| Responsive evidence của artifact Web hiện tại | 106 desktop, 2 mobile, 0 tablet | Chưa case nào có đủ bộ desktop + mobile + tablet |
| Auth E2E | 14 case: 11 PASS, 3 REUSED | Có Email Pickup và ảnh browser; chưa có ảnh email do người test tự chụp |
| Mail manual | 0 case hoàn tất | Email Pickup tự động không thay thế checklist chụp mailbox thủ công |
| Full Web | Chưa hoàn tất | Chưa được tuyên bố full route/binding/permission coverage |

WEB_E2E_PROGRESS.md khai báo run 20261007T095524.751804Z; file
artifacts/web-latest.json hiện trỏ tới artifact directory
20261007T131759.748829Z. Khi dùng làm release evidence phải ghi đúng run ID
đang được mở và cập nhật lại bảng này, không gộp hai run một cách im lặng.

### 0.4 Chuẩn responsive bắt buộc

Mọi case có UI phải có đủ ba viewport sau. Đây là điều kiện của evidence, không
phải chỉ là một bài smoke riêng:

| Tên viewport | Kích thước chuẩn | Tối thiểu |
| --- | ---: | --- |
| Desktop | 1440 × 960 | 1 screenshot sau mỗi trạng thái/assertion quan trọng |
| Mobile | 390 × 844 | 1 screenshot sau mỗi trạng thái/assertion quan trọng |
| Tablet | 768 × 1024 | 1 screenshot sau mỗi trạng thái/assertion quan trọng |

Case chỉ có API/DB và không có UI được ghi N/A kèm lý do. Với mọi case còn lại,
thiếu một trong ba viewport thì responsive evidence là PLANNED và case chưa đạt
release PASS. Artifact hiện tại mới có 106 desktop và 2 mobile; chưa có tablet.

## 1. Tiêu chuẩn để đánh dấu một case PASS

Một case chỉ được đánh [x] PASS nếu có đủ các mục sau:

- [ ] Case ID, parent scenario, app, module, route, actor và run ID được ghi rõ.
- [ ] Precondition và fixture ID đã được kiểm tra trước khi thao tác.
- [ ] Từng bước trong flow được thực hiện đúng thứ tự; không bỏ qua bước chỉ vì
      UI đã hiển thị sẵn.
- [ ] Có assertion UI có ý nghĩa: text, state, disabled, focus, route, modal,
      loading, empty, error hoặc giá trị thực tế.
- [ ] Có assertion API/DB/resource ID sau mutation; status code, response và
      ownership đúng.
- [ ] Sau mỗi save/mutation có reload hoặc đọc API để kiểm tra persistence.
- [ ] Có screenshot sau khi DOM/trạng thái ổn định ở đủ Desktop 1440×960,
      Mobile 390×844 và Tablet 768×1024. Ảnh phải thuộc đúng
      application/module/flow/case/viewport/step.
- [ ] Nếu flow có email, đã hoàn thành mục MAIL-MANUAL tương ứng hoặc case được
      giữ PARTIAL/PLANNED; Email Pickup tự động không đủ để đánh PASS manual.
- [ ] Đã kiểm tra cleanup/restore và không tạo duplicate resource ngoài dự kiến.
- [ ] Không có page error, success giả, retry vô hạn, request mutation trùng hoặc
      dữ liệu rò sang actor khác.

### 1.1 Hồ sơ tối thiểu của một case

Mỗi case trong report hoặc sổ tay phải có các trường:

~~~text
ID
Parent scenario
Application / module / route
Status
Run ID và người test
Actor
Precondition / fixture / checkpoint
Input data
Steps
Expected
Observed
UI assertion
API status / endpoint / resource ID
DB hoặc storage assertion nếu có
Screenshot paths
MAIL-MANUAL status và screenshot paths nếu có
Cleanup / restore
Issue link nếu FAIL hoặc BLOCKED
~~~

### 1.2 Quy ước evidence

Evidence tự động:

~~~text
frontend/e2e_testing/screenshots/<ngày-run>/<nhóm>/<user|admin|all>/<case-id>/<viewport>-<step>.png
frontend/e2e_testing/artifacts/runs/<run-id>/report.json
frontend/e2e_testing/artifacts/runs/<run-id>/http-statuses.json
~~~

Evidence mail thủ công:

~~~text
frontend/e2e_testing/manual-evidence/<run-id>/mail/<case-id>/01-inbox-row.png
frontend/e2e_testing/manual-evidence/<run-id>/mail/<case-id>/02-message-body.png
frontend/e2e_testing/manual-evidence/<run-id>/mail/<case-id>/03-link-result.png
~~~

Thư mục manual evidence có thể nằm ngoài artifact tự động, nhưng phải được link
trong case record và backup cùng run. Không commit password, cookie, JWT,
verification token đầy đủ hoặc thông tin người dùng thật.

## 2. Actor, môi trường và dữ liệu

### 2.1 Actor bắt buộc

| Actor | Mục đích | Trạng thái chuẩn bị |
| --- | --- | --- |
| GUEST | Route public, guard, CTA yêu cầu login | Có thể dùng browser context chưa đăng nhập |
| MEMBER | User đã verify, không sở hữu resource | Có fixture riêng cho full suite |
| OWNER | Tạo channel, video, playlist và sửa resource của mình | Runner hiện có account synthetic owner |
| REVIEWER_1 | Claim và xử lý moderation/report | Cần actor riêng |
| REVIEWER_2 | Appeal và separation of duties | Cần actor riêng |
| ADMIN | Admin thường, không tự động là super admin | Cần kiểm tra permission bị thu hồi |
| SUPER_ADMIN | RBAC, recommendation config, system-level operation | Cần actor riêng |

Runner hiện tại có thể tạm cấp role Admin cho cùng một user_id rồi restore.
Điều đó chỉ phục vụ fixture đăng nhập, không chứng minh full RBAC. Không đánh
PASS cho case multi-actor nếu vẫn dùng một account.

### 2.2 Dữ liệu fixture

| Fixture | Yêu cầu |
| --- | --- |
| Owner account | Email synthetic cố định, verified sau AUTH-VERIFY-01, ID lưu checkpoint |
| Member account | Khác owner; dùng để kiểm tra không leak và permission |
| Reviewer 1/2 | Hai identity khác nhau để kiểm tra claim conflict và SoD |
| Channel chính | Giữ xuyên các flow; không xóa trong cleanup |
| Channel phụ | Dùng cho delete, suspend, ban và restore |
| Video ready | Có duration, dimensions, audio/frame và playback thật |
| Video private/unlisted/hidden/removed/processing | Mỗi state phải có resource ID riêng |
| Playlist chính/phụ | Playlist phụ dùng cho delete; video nguồn không bị xóa theo |
| User dataset > 50 | Dùng cho pagination, total, filter, duplicate và reset page |
| Email mailbox | Mailbox synthetic do người test chỉ định; không dùng mail production |

Quy tắc dữ liệu:

- Không hardcode ID ngẫu nhiên; lấy ID từ checkpoint hoặc fixture manifest.
- Mỗi mutation phải ghi resource ID trước và sau.
- Không retry create bằng email/resource mới chỉ vì timeout.
- Cleanup phải có cancel trước confirm ở các destructive action.
- Sau test lỗi hoặc bị kill phải chạy restore fixture trước khi kết luận.

## 3. Quy trình kiểm tra email thủ công

Các case có email bắt buộc có hai lớp evidence:

1. Automation kiểm tra trigger, API response, Email Pickup hoặc trạng thái backend.
2. Người test mở mailbox thật của môi trường test và tự chụp ảnh email.

Ảnh browser hiển thị trang verify thành công **không** phải ảnh email. Không đánh
MAIL-MANUAL PASS chỉ vì Email Pickup đã nhận được message.

### 3.1 Các bước người test phải làm

1. Ghi case ID, run ID, timezone và mailbox synthetic.
2. Xóa hoặc tách các message cũ cùng subject để không đọc nhầm email.
3. Trigger flow từ UI thật; ghi thời điểm bấm submit.
4. Mở mailbox bằng UI; không dùng ảnh dựng, HTML tự tạo hoặc chỉ đọc database.
5. Chụp hàng email có sender, recipient, subject, timestamp và message ID nếu
   mailbox hiển thị.
6. Mở đúng message, chụp body có nội dung, CTA/link, cảnh báo hết hạn và
   footer/sender.
7. Mask password, cookie và phần lớn verification token. Nếu cần chứng minh link,
   giữ domain/path và 4 ký tự cuối token, không công khai token đầy đủ.
8. Mở link bằng browser test context; chụp trạng thái thành công hoặc lỗi.
9. Với link hết hạn, sai hoặc dùng lại, chụp email/link result và ghi status code
   hoặc message hiển thị.
10. Ghi screenshot path vào case record; nếu không nhận được email, đánh FAIL hoặc
    BLOCKED theo nguyên nhân, không đánh PASS vì UI có toast.

### 3.2.1 Responsive evidence cho case có email

- [ ] Trang UI khởi tạo email có screenshot Desktop 1440×960.
- [ ] Trang UI khởi tạo email có screenshot Mobile 390×844.
- [ ] Trang UI khởi tạo email có screenshot Tablet 768×1024.
- [ ] Mailbox/message body được chụp thủ công; nếu mailbox hỗ trợ responsive thì
      chụp ít nhất Desktop và Mobile, ghi N/A có lý do nếu Tablet không khả dụng.
- [ ] Trang kết quả khi bấm link email có đủ ba viewport như một UI case bình thường.

### 3.3 Sổ email phải kiểm tra thủ công

| Checkbox | Case | Trigger cần kiểm tra | Evidence bắt buộc | Status |
| --- | --- | --- | --- | --- |
| [ ] | AUTH-REG-03 | Đăng ký thành công gửi verification mail | Inbox row, full body, verify result | PLANNED |
| [ ] | AUTH-VERIFY-01 | Link verify đúng recipient | Body/link, success, DB verified | PLANNED |
| [ ] | AUTH-EXT-01 | Forgot password gửi OTP, nhập mã và đặt lại mật khẩu | Inbox row, OTP đúng recipient, reset success, mật khẩu mới đăng nhập được | PLANNED |
| [ ] | AUTH-EXT-02 | Reset OTP hết hạn/sai/replay | Email và từng error result | PLANNED |
| [ ] | U-CHANNEL.04 | Owner invite member/editor | Invite mail, accept/decline, invitation ID | PLANNED |
| [ ] | U-CHANNEL.05 | Notification/report/appeal nếu sản phẩm gửi mail | Mail và target/status đúng | PLANNED |
| [ ] | A-USERS.02 | Lock/unlock hoặc role change notification nếu có | Mail recipient đúng actor | PLANNED |
| [ ] | A-MODERATION.02 | Reject/escalate thông báo creator | Mail, reason/policy/version, status | PLANNED |
| [ ] | A-MODERATION.04 | Appeal decision thông báo owner | Mail, evidence/result, effective state | PLANNED |
| [ ] | A-MODERATION.05 | Strike/lock notification nếu có | Mail, severity, expiry, target | PLANNED |
| [ ] | U-PLAN.02 | Payment/entitlement notification nếu có | Mail, payment ID, entitlement state | BLOCKED nếu thiếu sandbox |

Nếu feature không hề có email binding, người test phải ghi N/A — verified no
email contract kèm source/API evidence. Không tự động coi thiếu email là PASS.
## 4. Ma trận route và guard

Mỗi route/dynamic route phải kiểm tra đủ: direct URL, link UI, reload,
back, forward, guard với guest/đủ quyền/thiếu quyền, ID không tồn tại và API
error. Route public không có permission branch thì ghi rõ N/A kèm lý do.

| App | Route group | Flow liên quan | Status roll-up | Bằng chứng hiện có / thiếu |
| --- | --- | --- | --- | --- |
| User | /login, /register, /verify-email, /forgot-password, /reset-password | AUTH, AUTH-EXT | PARTIAL | Core auth có evidence; forgot/reset và manual mail còn thiếu |
| User | /, /home, /explore, /search, wildcard | U-DISCOVERY | PARTIAL | Smoke và empty search có; filters/pagination/recommendation còn thiếu |
| User | /subscriptions | U-SUBSCRIPTION | PARTIAL | Route smoke và subscribe cơ bản có; đầy đủ tab/ownership còn thiếu |
| User | /playlists, /playlists/:id, /channel/:handle/playlists, /channel/:handle/playlists/:id | U-PLAYLIST | PARTIAL | Create/edit/access/item/remove/visibility có; multi-item reorder và pagination còn thiếu |
| User | /channel/create, /channel/:handle, /channel/:handle/customize | U-CHANNEL | PARTIAL | Basic/public/branding/invite có; toàn bộ role/restriction/delete còn thiếu |
| User | /watch/:id | U-WATCH | PARTIAL | Playback/private/reaction/comment/rating có; toàn bộ state/download/report còn thiếu |
| User | /terms, /privacy, /guidelines, /policies | U-POLICY | PARTIAL | Smoke/API policy có; UI current-version và PDF/keyboard/mobile còn thiếu |
| User | /plans, /plans/accept-invite, /plans/:planId, /my-plan | U-PLAN | BLOCKED/PARTIAL | Catalog cơ bản có; payment/webhook/entitlement bị BLOCKED |
| User | /account, /profile, /history, /liked, /library | U-ACCOUNT, U-LIBRARY | PARTIAL | Account và smoke có; session/download/library pagination còn thiếu |
| User | /channel-invitations, /studio/invitations | U-CHANNEL | PARTIAL | Invite/accept có; mail/đủ permission matrix còn thiếu |
| User | /studio, /studio/setup, /studio/overview, /studio/content, /studio/content/:id/edit, /studio/upload, /studio/analytics, /studio/comments, /studio/subtitles, /studio/settings | U-STUDIO | PARTIAL | Route smoke và upload fixture có; wizard nâng cao/chunk/captions/settings còn thiếu |
| Admin | /login, /verify-email, /forgot-password, /reset-password, /forbidden | AUTH, X-ACCESS | PARTIAL | Admin deny/login/logout có; reset/permission matrix còn thiếu |
| Admin | /users, /channels, /videos, /roles, /plans, /topics, /policies | A-* | PARTIAL | Smoke và một số CRUD/list có; mutation đầy đủ còn thiếu |
| Admin | /moderation/videos, /moderation/reports, /moderation/appeals, /moderation/strikes | A-MODERATION | PARTIAL | Approve cơ bản có; report/appeal/strike/SoD còn thiếu |
| Admin | /cf-seeder, /recommendations, /account, root/wildcard | A-CF, A-RECOMMENDATION | BLOCKED/OUT_OF_SCOPE | CF Seeder/simulation bị loại; recommendation jobs thiếu service |

Checklist route cho từng route group:

- [ ] Mở bằng direct URL khi guest.
- [ ] Mở qua link/menu UI và xác nhận active route đúng.
- [ ] Reload tại route đó, giữ đúng identity và resource.
- [ ] Back rồi forward; không mất query, filter, tab hoặc checkpoint.
- [ ] Actor đủ quyền thấy đúng dữ liệu và action.
- [ ] Actor thiếu quyền nhận 401/403 đúng; không lộ body/sourceURL/thumbnail nhạy cảm.
- [ ] Resource ID không tồn tại nhận 404/empty state đúng.
- [ ] API 4xx/5xx kết thúc loading, có error và retry bounded.
- [ ] Alias/redirect/wildcard đi đúng đích và không tạo history loop.

## 5. AUTH — đã có evidence và phần mở rộng

### 5.1 Auth core

| Checkbox | ID | Actor/route | Luồng và validation bắt buộc | Evidence hiện có |
| --- | --- | --- | --- | --- |
| [x] | AUTH-REG-01 | Guest /register | Để trống tất cả; submit; aria-invalid/required hiển thị; không POST | PASS |
| [x] | AUTH-REG-02 | Guest /register | Điền confirm password khác; lỗi mismatch; không tạo user/không POST thành công | PASS |
| [x] | AUTH-REG-03 | Guest /register | Điền dữ liệu hợp lệ; POST 201 đúng một user pending; ghi account ID; trigger email | REUSED; automation evidence có, MAIL-MANUAL còn PLANNED |
| [x] | AUTH-LOGIN-01 | Unverified user /login | Login trước verify; 403 đúng contract; không vào account | REUSED |
| [x] | AUTH-VERIFY-01 | Guest /verify-email?token=... | Mở link từ email Pickup; DB verified; UI success; token không được log đầy đủ | REUSED; MAIL-MANUAL còn PLANNED |
| [x] | AUTH-REG-04 | Existing email/username | Submit lại cùng data; 409 EMAIL_ALREADY_EXISTS hoặc USERNAME_ALREADY_EXISTS; account count vẫn 1 | PASS |
| [x] | AUTH-LOGIN-02 | Guest /login | Bỏ trống; required error; không POST | PASS |
| [x] | AUTH-LOGIN-03 | User /login | Password sai một lần; 401 INVALID_CREDENTIALS; không spam lockout | PASS |
| [x] | AUTH-LOGIN-04 | Verified user /login | 200 đúng user ID; HttpOnly cookie; không token/password trong storage; desktop/mobile | PASS |
| [x] | AUTH-LOGIN-05 | User /account | Reload; session/identity phục hồi đúng; không rơi về login | PASS |
| [x] | AUTH-LOGIN-06 | User account/logout | Logout và mở lại protected route; redirect login; cookie/session bị xóa | PASS |
| [x] | AUTH-ADMIN-01 | Normal user → Admin login | 403 ADMIN_ACCESS_DENIED; không vào Admin | PASS |
| [x] | AUTH-ADMIN-02 | Temporary admin fixture | Admin login đúng ID; admin cookie HttpOnly; profile đúng; role restore sau flow | PASS |
| [x] | AUTH-ADMIN-03 | Admin logout | Logout rồi mở /users; guard về login; role fixture restore | PASS |

### 5.2 AUTH-EXT — chưa đủ evidence

| Checkbox | ID | Luồng chi tiết | Validation bắt buộc | Status |
| --- | --- | --- | --- | --- |
| [ ] | AUTH-EXT-01 | Forgot password bằng email đã có và email chưa có; nhập OTP nhận được qua mail; reset mật khẩu; login bằng mật khẩu mới/cũ | Không leak account existence; OTP đúng recipient; reset success; password cũ fail; manual mail screenshot | PLANNED |
| [ ] | AUTH-EXT-02 | Password/email boundary, hoa-thường, whitespace, email invalid, password ngắn/dài, confirm/show-hide, double click, network fail | Không duplicate; không success giả; lỗi đúng field; request count đúng | PLANNED |
| [ ] | AUTH-EXT-03 | Verify token sai, thiếu, hết hạn, đã dùng; reset OTP thiếu/sai/hết hạn/đã dùng; back/forward; return URL | Không replay; không open external origin; reset chỉ thành công với OTP hợp lệ; admin không mở registration | PLANNED |
| [ ] | AUTH-EXT-04 | Lock/suspend/delete sau khi login; revoke session; đổi role giữa session; hai tab User/Admin | Refresh/current route/API bị chặn đúng; không leak dữ liệu; cookie không trộn platform | PLANNED |
| [ ] | AUTH-EXT-05 | Google OAuth sandbox: cancel, credential sai/expired, unverified, linked, blocked | Nếu thiếu dependency đánh BLOCKED; local auth tắt Google không được tính OAuth PASS | BLOCKED nếu chưa có sandbox |

## 6. User Web — scenario cards

Status của các dòng dưới là roll-up. Evidence atomics đã chạy được liệt kê ở
Section 10; không lấy một case con để đánh PASS cho cả dòng.

### 6.1 Account và profile

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-ACCOUNT.01 | OWNER /account, fixture owner | Đọc profile; mở edit; nhập tên/bio Unicode; cancel; mở lại; save; upload avatar hợp lệ/sai type/sai size; reload. Cancel không persist; save lưu DB; ảnh lỗi không đổi avatar; loading/error đúng. | Read/cancel/validation/save/restore/mobile | PARTIAL |
| U-ACCOUNT.02 | OWNER account tabs | Lần lượt notifications, privacy, downloads, billing, advanced; đổi từng option; save; reload; kiểm tra language/theme, subscription privacy, notification group, copy ID. Nút chưa hỗ trợ phải disabled/giải thích. | Notifications và privacy | PARTIAL |
| U-ACCOUNT.03 | OWNER hai browser context | Đổi password; revoke session khác/current; logout others; refresh cả hai context. Chỉ session mục tiêu bị revoke; current revoke logout; không revoke ID người khác. | Chưa có full session evidence | PLANNED |
| U-ACCOUNT.04 | OWNER downloads/plans | Chọn từng quality, smart download; delete one/all/cancel; đổi plan. Download state xóa đúng; entitlement/quota kiểm soát; không tạo download giả. | Chưa có | PLANNED |

### 6.2 Channel, membership và branding

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-CHANNEL.01 | OWNER chưa có channel | Nhập name/handle; duplicate, ký tự sai, whitespace; validate; create; lưu channel ID; chạy lại mở fixture cũ. Chỉ một channel đúng owner; route handle đúng. | Create reused, validation | PARTIAL |
| U-CHANNEL.02 | OWNER customize/settings | Avatar/banner/watermark PNG hợp lệ; sai MIME/size/zero byte; title/description/links thêm/sửa/reorder/remove; cancel/save/reload. Không báo toàn bộ thành công nếu một upload fail. | Basic metadata, contact clear, PNG branding | PARTIAL |
| U-CHANNEL.03 | GUEST/MEMBER public channel | Home/Videos/Playlists; search/sort/page/share/copy; handle sai; private/deleted/empty/loading/notfound. Không leak nội dung private; totals và pagination đúng. | Public channel cơ bản | PARTIAL |
| U-CHANNEL.04 | OWNER + MEMBER + REVIEWER | Owner invite bằng email; member accept/decline; owner đổi role/remove/revoke; kiểm tra từng permission upload/edit/comments/analytics/settings ở UI và API. Invitation replay bị chặn; owner không mất quyền. | Invite/accept/role/remove | PARTIAL; manual mail PLANNED |
| U-CHANNEL.05 | MEMBER/OWNER/GUEST | Subscribe và từng notification setting; report channel/video/comment; appeal moderation; kiểm tra guest CTA login. Count không cộng lặp; target/status đúng. | Subscribe | PARTIAL |
| U-CHANNEL.06 | Channel phụ và channel chính | Delete cancel/confirm; suspended/blocked channel thử upload/play; dữ liệu account khác không đổi. | Chưa có | PLANNED |

### 6.3 Discovery và subscriptions

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-DISCOVERY.01 | GUEST/OWNER /home | Recommendation personalized/anonymous; mở watch rồi back; tắt service; fallback dùng data thật; không hiện private/hidden. | Home smoke | PLANNED |
| U-DISCOVERY.02 | GUEST/MEMBER /explore, /search | Query có dấu/không dấu; category/sort/duration/date; reset; next/previous/pageSize; empty; Unicode/XSS text. Query params, result, count và page reset đúng. | Empty query | PARTIAL |
| U-DISCOVERY.03 | GUEST/MEMBER trending/carousel | Prev/next không vượt biên; creator card → channel → subscribe; gõ filter nhanh; response cũ không ghi đè query mới; focus/scroll đúng. | Chưa có | PLANNED |
| U-SUBSCRIPTION.01 | GUEST/MEMBER /subscriptions | Guest CTA login; member subscribe/unsubscribe; tab/video/notification; dữ liệu chỉ thuộc member; private subscription không leak. | Route smoke và subscribe cơ bản | PARTIAL |

### 6.4 Playlist và library

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-PLAYLIST.01 | OWNER /playlists | Create với public/private/unlisted; name empty/duplicate/double click; edit cancel/save/reload. Retry không tạo duplicate; URL/API enforce visibility. | Create reused, edit, visibility, cancel | PARTIAL |
| U-PLAYLIST.02 | OWNER playlist + video fixtures | Add từ Watch picker; add trùng; reorder nhiều item lên/xuống; remove/cancel/add lại; reload; tombstone video. Thứ tự persist; user khác không sửa. | Add/remove | PARTIAL; reorder nhiều item PLANNED |
| U-PLAYLIST.03 | GUEST/MEMBER public playlist | Play all/shuffle/next/previous/onEnded; share/copy; chuyển nhanh A→B; empty/private/removed. Không phát sai stream; order hợp lệ. | Chưa có | PLANNED |
| U-PLAYLIST.04 | OWNER playlist phụ | Delete cancel/confirm; nhiều playlist; pagination/filter; chỉ xóa playlist phụ, không xóa video nguồn; total/page đúng. | Delete | PARTIAL |
| U-LIBRARY.01 | OWNER history/liked | Xem video tới mốc; history sort/search/filter/page; mở lại; liked theo rating. watchSeconds hữu hạn và đúng; count không cộng lặp. | History/liked smoke | PARTIAL |
| U-LIBRARY.02 | OWNER + other user | Like/dislike/clear; reload aliases /library và /profile; private/hidden/deleted không lộ source URL. | Chưa có full evidence | PLANNED |

### 6.5 Policy và plan

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-POLICY.01 | GUEST/MEMBER policy routes | Mở terms/privacy/guidelines/policies; group/search/detail; version/severity; print/download PDF nếu có. Nội dung phải là version published; PDF có data thật. | Smoke và API v2 | PARTIAL |
| U-POLICY.02 | GUEST/MEMBER desktop/mobile/keyboard | Privacy toggle disabled; support/legal links; keyboard focus/target. Không toast save giả; disabled phải giải thích. | Chưa có | PLANNED |
| U-PLAN.01 | GUEST/MEMBER /plans | Từng active/inactive plan; detail; feature/price/duration/max member; current plan; copy/share. Giá/đơn vị từ API; inactive không mua. | Plan catalog | PARTIAL |
| U-PLAN.02 | MEMBER payment sandbox | Create payment; lưu payment ID; QR; pending→paid webhook; close/reopen; expired/canceled/failed. Chỉ paid mới cấp entitlement, đúng một lần; không chuyển tiền thật. | Chưa có sandbox | BLOCKED |
| U-PLAN.03 | OWNER + MEMBER | Invite member; accept-invite; allocate/edit/remove/revoke; renew. Tổng quota không vượt; không giảm dưới usedBytes; invite sai/expired/replay bị chặn. | Chưa có | PLANNED |
| U-PLAN.04 | OWNER downgrade/expiry fixture | Thử upload/download/background/PiP sau downgrade/expiry; reload; backend và UI cùng enforce. | Chưa có | BLOCKED nếu thiếu payment dependency |

### 6.6 Studio

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-STUDIO.01 | OWNER /studio/upload, video thật | File input thật; đọc duration/dimensions bằng ffprobe/browser metadata; lưu upload ID/video ID. | Upload reused | PARTIAL |
| U-STUDIO.02 | OWNER upload wizard | Next/back/cancel qua title, description, category, tags, language, thumbnail upload/frame/seek, chapters, privacy, comments, playlist, review. Required/biên/quá dài/trùng/out-of-duration/sai ảnh lỗi đúng field; scheduled/auto subtitle disabled đúng. | Upload required/schedule disabled | PARTIAL |
| U-STUDIO.03 | OWNER draft/publish | Draft → reload → restore/cancel; publish một lần; lưu ID ngay; progress; poll processing→ready; GET playback; quality không vượt source; chunkSize lấy từ response. | Playback/transcode fixture | PARTIAL |
| U-STUDIO.04 | OWNER và thiếu role/quota | Missing file, wrong extension, zero byte, quota, role, cancel trước/trong upload, offline, duplicate request, restart, probe/FFmpeg/storage fail, retry cùng upload ID. Không success giả/duplicate manifest. | Chưa có | PLANNED |
| U-STUDIO.05 | OWNER content/edit | Filter status/visibility/category/search/page; edit metadata/thumbnail/tags/playlist; save/reload; chuyển video nhanh không nhận stale response; playlist lỗi tách success/fail. | Chưa có full | PLANNED |
| U-STUDIO.06 | OWNER moderation states | Pending/processing/rejected/ready/removed; retry/delete/cancel; appeal reason/evidence; purge không restore. | Chưa có | PLANNED |
| U-STUDIO.07 | OWNER analytics | Mọi tab/time range/chart; dataset nhiều trang; views khác watchSeconds; export file/header/row/value và CSV formula escaping; empty/loading/error không dùng số mẫu. | Analytics smoke | PARTIAL |
| U-STUDIO.08 | OWNER/MEMBER/GUEST comments | Filter/status/sort/page; reply/edit/hide/delete/moderation theo permission; reload/watch phía member; disabled comments chặn API mutation. | Comments cơ bản | PARTIAL |
| U-STUDIO.09 | OWNER subtitles/settings/invitations | Tất cả button hiện/disabled; placeholder báo đúng; settings/invitations dùng đủ/thiếu từng studio permission. | Route smoke | PARTIAL |

### 6.7 Watch và tương tác

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| U-WATCH.01 | GUEST/MEMBER ready video | Metadata; play/pause, seek, volume/mute, speed, từng quality/chapter, fullscreen/PiP theo entitlement. So currentTime/duration/paused/rendition thật, không chỉ icon. | Playback | PARTIAL |
| U-WATCH.02 | MEMBER playlist/history fixture | Watch tới mốc; pause/navigate/resume; history/progress; view count/rating/watchSeconds; reload/retry; playlist A→B next/previous/onEnded. Không double count hoặc phát nhầm stream. | Chưa có full | PLANNED |
| U-WATCH.03 | MEMBER/GUEST | Like/dislike/clear; subscribe/notification/unsubscribe; share/copy; add/remove playlist; download từng quality/quota/plan; guest mutation CTA login giữ context. | Reaction | PARTIAL |
| U-WATCH.04 | OWNER/MEMBER/GUEST comment | Reply/edit/cancel/save/delete/cancel; reaction/sort/page; report comment/video/channel; owner/member khác bị chặn đúng. | Comment/create/delete/access | PARTIAL; reply/report/pagination PLANNED |
| U-WATCH.05 | GUEST/MEMBER/OWNER mọi video state | Public/private/unlisted/age-restricted/hidden/removed/deleted/channel suspended/processing/missing rendition/network. Auth đúng; không leak sourceURL/thumbnail; retry không loop. | Private playback | PARTIAL |

## 7. Admin Web — scenario cards

Admin phải chạy với actor cố định có đủ quyền và actor bị thu hồi quyền. Admin
không đồng nghĩa Super Admin. Mọi action cần kiểm tra cả direct URL và API.

| ID | Actor/route và precondition | Flow, validation và expected | Evidence hiện có | Status |
| --- | --- | --- | --- | --- |
| A-ACCOUNT.01 | ADMIN account/preferences/sessions | Profile, theme, language; reload; revoke/cancel/current/logout others. Session mục tiêu đúng; guard đúng sau revoke. | Read/reload/mobile | PARTIAL |
| A-USERS.01 | ADMIN /users, dataset > 50 | Search; mọi role/status/filter; pageSize/page; detail; statistics date range/custom. Total/page/filter reset đúng; drawer không stale. | Search/filter/pagination | PARTIAL |
| A-USERS.02 | ADMIN + MEMBER fixture | Lock/unlock reason; role change; notification; refresh phiên User; không self-elevate. | Lock/unlock | PARTIAL |
| A-CHANNELS.01 | ADMIN /channels | Status/tab/search/sort/pageSize/page/detail/export CSV; Unicode/formula escape; time filter disabled nếu chưa hỗ trợ. | List/detail/CSV | PARTIAL |
| A-CHANNELS.02 | ADMIN channel phụ | Ban/suspend/unban/delete với reason/cancel; User reload/watch/upload; channel chính không bị ảnh hưởng. | Chưa có | PLANNED |
| A-VIDEOS.01 | ADMIN /videos | Mọi status/visibility/category/date/sort/pageSize; detail/preview/statistics/export. Totals và CSV đúng định nghĩa. | Smoke only | PLANNED |
| A-VIDEOS.02 | ADMIN video fixture | Edit title/description/category; clear hợp lệ; chuyển video nhanh; cancel/save/reload; permission/audit đúng. | Chưa có | PLANNED |
| A-VIDEOS.03 | ADMIN video phụ | Hide/unhide/remove/restore từng reason/cancel; User watch/creator state; purged không restore. | Chưa có | PLANNED |
| A-VIDEOS.04 | ADMIN Add Video nếu button hiện | Nhập title/visibility; submit; DB/list phải có record. Nếu submitAddVideo() chỉ đóng modal/notice và không gọi API thì ghi FAIL/issue, không coi toast là PASS. | Chưa có | PLANNED |
| A-RBAC.01 | SUPER_ADMIN /roles | Search; create role; code/name/description/reason; permission search/toggle; save; assign member; login lại. API allow/deny theo matrix; session update/revoke. | Create/edit | PARTIAL |
| A-RBAC.02 | SUPER_ADMIN role phụ | Edit/delete; code trùng; system role/in-use; missing permission; self-elevation; cancel. Constraint/error rõ; channel permission không nhầm global permission. | Chưa có full | PLANNED |
| A-PLANS.01 | ADMIN plans | Từng tab/template; create price/duration/quota/maxMembers/features/order; edit/archive/reload. Một lần tạo một ID; inactive không mua. | Create/edit/archive | PARTIAL |
| A-PLANS.02 | ADMIN + User | Mỗi feature download/background/PiP/upload; numeric 0/negative/overflow/decimal; User catalog/entitlement. | Unit conversion/feature partial | PARTIAL |
| A-TOPICS.01 | ADMIN topics/tags | Search; create/edit/status/archive/delete; unique/biên/permission; item đang dùng không mất liên kết. | Category/tag CRUD | PARTIAL |
| A-POLICY.01 | ADMIN policies | Group/search/detail; draft/version; create/edit/publish; User policy UI/API; published immutable; moderation decision giữ version. | Create/publish/API | PARTIAL |
| A-CF.01 | ADMIN CF Seeder | Existing account pool; import JSON/template; picker/folder fallback; category/quality; invalid JSON/duplicate/missing permission. | User excluded | OUT_OF_SCOPE |
| A-CF.02 | ADMIN CF Seeder | Upload seed; chunk/status/history/log; cancel/reset/download; restart/recover; không tạo lại data khi chỉ poll. | User excluded | OUT_OF_SCOPE |
| A-RECOMMENDATION.01 | SUPER_ADMIN recommendation | Registry/model; active average/weighted; weights; save/reload; non-super admin denied; config thực sự feed. | Smoke only | BLOCKED |
| A-RECOMMENDATION.02 | SUPER_ADMIN jobs/matrix | Preview/page/export CSV; update job/poll/history/log/error/cancel; job ID persist; thiếu service BLOCKED. | Smoke only | BLOCKED |
| A-RECOMMENDATION.03 | SUPER_ADMIN simulation | Fixed manifest; từng signal/rate/cluster/comment; start/stop/history; không bot/duplicate khi reload. | User excluded | OUT_OF_SCOPE |

## 8. Moderation xuyên User → Admin → User

Mỗi flow phải dùng resource ID xuyên suốt và chụp evidence ở cả User lẫn Admin.
Không dùng một reviewer cho cả quyết định moderation và appeal của cùng target.

| ID | Luồng chi tiết | Validation / evidence | Status |
| --- | --- | --- | --- |
| A-MODERATION.01 | Video pending → REVIEWER_1 queue/filter/page → claim → policy/reason/notes → approve → User thấy ready và watch được | Claim idempotent; reviewer2 concurrent không ghi đè; bulk partial failure; User/Admin cùng video ID | PARTIAL; approve cơ bản REUSED |
| A-MODERATION.02 | Video phụ → reject/escalate từng decision/policy/version → User nhận notice và creator status | Reason/policy bắt buộc; release/claim giữ history; manual mail nếu có | PLANNED |
| A-MODERATION.03 | Member report video/comment/channel → reviewer claim/release/classify/resolve | Dismiss, warn, age_restrict, recommendation_restricted, hide, remove, upload_restriction, strike, lock_channel, escalate; target/effective/audit/idempotent đúng | PLANNED |
| A-MODERATION.04 | OWNER appeal reason/evidence → REVIEWER_2 claim → mở đúng target → approve/reject/escalate | SoD; reviewer đã quyết định không tự duyệt appeal; evidence hết quyền/broken; purge không restore; mail nếu có | PLANNED |
| A-MODERATION.05 | Strike create/revoke severity/policy/note/restriction days → lock/unlock channel → User upload/watch | Active/expired/revoked không âm; dùng backend clock; audit và notice đúng | PLANNED |
| X-MODERATION.01 | Claim hết hạn, reviewer conflict, duplicate decision, locked/deleted channel, legacy report, duplicate/processed appeal, DB rollback | Không success giả; rollback không mất history; evidence hai app cùng resource ID | PLANNED |

## 9. Ma trận validation dùng cho mọi nhóm

Các bảng scenario chỉ nêu dữ liệu đặc thù. Những mục dưới đây vẫn phải được
áp dụng cho từng binding phù hợp trong inventory.

### X-SHELL — shell/header/navigation

- [ ] Header, sidebar, menu, drawer, search, notification, avatar.
- [ ] Open/close/cancel/dismiss, backdrop, Escape, active route.
- [ ] Notification read, read-all và link target đúng.
- [ ] Reconnect realtime không nhân duplicate item.
- [ ] Copy dùng clipboard thật; download/export tạo file thật khi binding có tồn tại.

### X-ACCESS — permission và data isolation

- [ ] Guest, owner, member, reviewer1, reviewer2, admin thiếu quyền và super_admin.
- [ ] Direct URL, link UI và API đều enforce cùng permission.
- [ ] 401/403/404 đúng; không leak private data, source URL, thumbnail, IDs không cần thiết.
- [ ] Revoke quyền khi đang mở tab; refresh và mutation sau revoke đều bị chặn.
- [ ] User A không đọc/sửa/xóa resource của User B qua URL, API hoặc stale drawer.

### X-FORM — input và mutation

- [ ] Required, empty, whitespace đầu/cuối, min/max length, Unicode, emoji.
- [ ] Enum từng lựa chọn; negative, zero, overflow, decimal và sai unit.
- [ ] URL invalid, javascript: hoặc external-origin, XSS text được render như text.
- [ ] CSV/spreadsheet formula escape cho value bắt đầu bằng =, +, -, @.
- [ ] Double click, Enter, cancel giữa request, retry và request lặp không duplicate.
- [ ] Stale response khi đổi resource/filter nhanh không ghi nhầm resource mới.
- [ ] File missing, zero byte, MIME sai, extension sai, size/quota vượt giới hạn.

### X-ERROR — network và trạng thái request

- [ ] Timeout và offline; loading kết thúc, không spinner vô hạn.
- [ ] 400, 401, 403, 404, 409, 422, 429, 500; message không lộ stack/secret.
- [ ] Retry bounded, retry đúng endpoint/resource, không duplicate mutation.
- [ ] Partial failure hiển thị phần thành công/thất bại riêng.
- [ ] Reload sau lỗi không biến error thành success giả.

### X-UI — viewport, accessibility và localization

- [ ] Desktop 1440×960, mobile 390×844 và tablet 768×1024.
- [ ] Chụp screenshot ở cả ba viewport; thiếu một viewport thì không đánh responsive PASS.
- [ ] Không overflow ngang; modal/drawer không bị cắt; button không bị che hoặc tràn.
- [ ] Light/dark; vi/en; long text; zoom.
- [ ] Keyboard order, focus visible, Escape, label/aria, disabled state.
- [ ] Empty, loading, error, no permission, processing và deleted state.
- [ ] Screenshot mỗi viewport chỉ khi assertion state đã ổn định.

### X-DATA — pagination và consistency

- [ ] Dataset một trang, nhiều trang, hơn 50 rows.
- [ ] Total/count/pageSize/page; first/last/next/previous.
- [ ] Filter, sort, reset filter trả về page đầu.
- [ ] Tất cả enum/status/order cần thiết.
- [ ] Cross-user isolation, duplicate, deleted/tombstone resource.
- [ ] Export có đúng scope được UI công bố, không tự đoán export toàn dataset.

### X-STATE — navigation/session/persistence

- [ ] Reload, back, forward, new tab, new browser context.
- [ ] Storage/auth restore; logout/revoke/role change giữa các tab.
- [ ] Crash/restart/resume dùng checkpoint; không tạo resource thứ hai.
- [ ] Sau mỗi save đọc lại API/DB cùng resource ID.
- [ ] Sau cancel/close/dismiss không persist dữ liệu chưa save.

### X-PERF — kế hoạch chưa chạy

- [ ] LCP/CLS, long task, interaction p95.
- [ ] Heap leak sau chuyển route và mở/đóng modal nhiều lần.
- [ ] Video startup/seek/buffering trên fixture thật.
- [ ] Không chờ networkidle vô hạn cho realtime/player.

## 10. Execution ledger — Web 108 case hiện có

Danh sách dưới đây lấy theo docs/WEB_E2E_PROGRESS.md. REUSED được đánh riêng
dù artifact JSON của runner có thể ghi status tổng là passed. Không dùng ledger
này để suy ra rằng các case PLANNED trong Section 6–9 đã hoàn tất.
Các PASS trong ledger là PASS chức năng của run lịch sử. Sau khi áp dụng chuẩn
responsive ở Section 0.4, mọi case vẫn phải hoàn thiện responsive evidence trước
khi được xem là release PASS.

### 10.1 Web — PASS chức năng, responsive còn pending

| Checkbox | App | Evidence case ID |
| --- | --- | --- |
| [x] | user | U-ACCOUNT-READ-01 |
| [x] | user | U-ACCOUNT-CANCEL-01 |
| [x] | user | U-ACCOUNT-MOBILE-01 |
| [x] | user | U-ACCOUNT-VALIDATION-01 |
| [x] | user | U-ACCOUNT-VALIDATION-02 |
| [x] | user | U-ACCOUNT-BIO-LIMIT-01 |
| [x] | user | U-ACCOUNT-SAVE-01 |
| [x] | user | U-ACCOUNT-RESTORE-01 |
| [x] | admin | A-ACCOUNT-READ-01 |
| [x] | admin | A-ACCOUNT-RELOAD-01 |
| [x] | admin | A-ACCOUNT-MOBILE-01 |
| [x] | user | U-CHANNEL-PUBLIC-01 |
| [x] | user | U-CHANNEL-SUBSCRIBE-01 |
| [x] | user | U-PLAYLIST-EDIT-01 |
| [x] | user | U-PLAYLIST-ACCESS-01 |
| [x] | user | U-HOME-SMOKE-01 |
| [x] | user | U-EXPLORE-SMOKE-01 |
| [x] | user | U-SUBSCRIPTIONS-SMOKE-01 |
| [x] | user | U-HISTORY-SMOKE-01 |
| [x] | user | U-LIKED-SMOKE-01 |
| [x] | user | U-PLANS-SMOKE-01 |
| [x] | user | U-TERMS-SMOKE-01 |
| [x] | user | U-PRIVACY-SMOKE-01 |
| [x] | user | U-GUIDELINES-SMOKE-01 |
| [x] | user | U-POLICIES-SMOKE-01 |
| [x] | user | U-DISCOVERY-EMPTY-01 |
| [x] | user | U-STUDIO-OVERVIEW-SMOKE-01 |
| [x] | user | U-STUDIO-CONTENT-SMOKE-01 |
| [x] | user | U-STUDIO-ANALYTICS-SMOKE-01 |
| [x] | user | U-STUDIO-COMMENTS-SMOKE-01 |
| [x] | user | U-STUDIO-SUBTITLES-SMOKE-01 |
| [x] | user | U-STUDIO-SETTINGS-SMOKE-01 |
| [x] | user | U-STUDIO-INVITATIONS-SMOKE-01 |
| [x] | user | U-ACCOUNT-NOTIFICATIONS-01 |
| [x] | user | U-ACCOUNT-PRIVACY-01 |
| [x] | user | U-WATCH-PLAYBACK-01 |
| [x] | user | U-WATCH-PRIVATE-01 |
| [x] | user | U-WATCH-REACTION-01 |
| [x] | user | U-WATCH-COMMENT-01 |
| [x] | user | X-COMMENT-ACCESS-01 |
| [x] | user | U-WATCH-COMMENT-DELETE-01 |
| [x] | user | U-CHANNEL-INVITE-01 |
| [x] | user | U-CHANNEL-ACCEPT-01 |
| [x] | user | U-CHANNEL-ROLE-01 |
| [x] | user | U-CHANNEL-REMOVE-01 |
| [x] | user | X-ERROR-SEARCH-01 |
| [x] | admin | A-USERS-SMOKE-01 |
| [x] | admin | A-CHANNELS-SMOKE-01 |
| [x] | admin | A-VIDEOS-SMOKE-01 |
| [x] | admin | A-RBAC-SMOKE-01 |
| [x] | admin | A-PLANS-SMOKE-01 |
| [x] | admin | A-SUBSCRIPTIONS-SMOKE-01 |
| [x] | admin | A-PAYMENTS-SMOKE-01 |
| [x] | admin | A-TOPICS-SMOKE-01 |
| [x] | admin | A-POLICY-SMOKE-01 |
| [x] | admin | A-MODERATION-SMOKE-01 |
| [x] | admin | A-REPORTS-SMOKE-01 |
| [x] | admin | A-APPEALS-SMOKE-01 |
| [x] | admin | A-STRIKES-SMOKE-01 |
| [x] | admin | A-NOTIFICATIONS-SMOKE-01 |
| [x] | admin | A-SYSTEM-REPORTS-SMOKE-01 |
| [x] | admin | A-LOGS-SMOKE-01 |
| [x] | admin | A-RECOMMENDATION-SMOKE-01 |
| [x] | admin | A-USERS-SEARCH-01 |
| [x] | admin | A-USERS-FILTER-01 |
| [x] | admin | A-TOPICS-EDIT-01 |
| [x] | admin | A-TOPICS-CANCEL-01 |
| [x] | admin | A-TOPICS-ARCHIVE-01 |
| [x] | user | X-TOPICS-ACCESS-01 |
| [x] | admin | A-TAGS-CREATE-01 |
| [x] | admin | A-TAGS-DELETE-01 |
| [x] | admin | A-PLANS-EDIT-01 |
| [x] | user | X-PLANS-CATALOG-01 |
| [x] | admin | A-PLANS-ARCHIVE-01 |
| [x] | admin | A-RBAC-EDIT-01 |
| [x] | user | X-RBAC-ACCESS-01 |
| [x] | user | X-MODERATION-WATCH-01 |
| [x] | user | U-PLAYLIST-ITEM-01 |
| [x] | user | X-PLAYLIST-EDIT-01 |
| [x] | user | U-PLAYLIST-REMOVE-01 |
| [x] | user | X-MODERATION-RESTORE-01 |
| [x] | admin | A-USERS-LOCK-01 |
| [x] | admin | A-USERS-UNLOCK-01 |
| [x] | user | X-POLICY-API-01 |
| [x] | admin | A-USERS-PAGINATION-01 |
| [x] | admin | A-CHANNELS-LIST-01 |
| [x] | admin | A-CHANNELS-CSV-01 |
| [x] | user | U-CHANNEL-VALIDATION-01 |
| [x] | user | U-CHANNEL-BASIC-01 |
| [x] | user | U-CHANNEL-CONTACT-CLEAR-01 |
| [x] | user | U-CHANNEL-BASIC-RESTORE-01 |
| [x] | user | U-CHANNEL-AVATAR-01 |
| [x] | user | U-CHANNEL-BANNER-01 |
| [x] | user | U-CHANNEL-WATERMARK-01 |
| [x] | user | U-PLAYLIST-VISIBILITY-01 |
| [x] | user | U-PLAYLIST-CANCEL-01 |
| [x] | user | U-PLAYLIST-DELETE-01 |
| [x] | user | U-WATCH-RATING-01 |

### 10.2 Web — REUSED, phải chạy lại nếu cần regression mới

| Checkbox | App | Evidence case ID | Lý do |
| --- | --- | --- | --- |
| [x] | user | U-CHANNEL-CREATE-01 | Fixture channel đã tồn tại; không create lại trong run |
| [x] | user | U-PLAYLIST-CREATE-01 | Fixture playlist đã tồn tại; không create lại trong run |
| [x] | user | U-STUDIO-UPLOAD-01 | Upload fixture đã tồn tại; không upload lại do timeout |
| [x] | admin | A-TOPICS-CREATE-01 | Topic fixture đã tồn tại |
| [x] | admin | A-PLANS-CREATE-01 | Plan fixture đã tồn tại |
| [x] | admin | A-RBAC-CREATE-01 | Role fixture đã tồn tại |
| [x] | user | X-MODERATION-PUBLISH-01 | Publish state đã có từ flow trước |
| [x] | admin | A-MODERATION-APPROVE-01 | Approve evidence được giữ từ flow trước |
| [x] | admin | A-POLICY-CREATE-01 | Policy v1 đã tồn tại |
| [x] | admin | A-POLICY-PUBLISH-01 | Policy v2 đã tồn tại |

### 10.3 Auth — evidence tách khỏi 108 Web case

| Checkbox | ID | Status | Evidence |
| --- | --- | --- | --- |
| [x] | AUTH-REG-01 | PASS | auth-results.md và screenshot |
| [x] | AUTH-REG-02 | PASS | auth-results.md và screenshot |
| [x] | AUTH-REG-03 | REUSED | Run đầu có 201 và ảnh; run sau không tạo lại |
| [x] | AUTH-LOGIN-01 | REUSED | Bằng chứng login unverified từ run đầu |
| [x] | AUTH-VERIFY-01 | REUSED | Email Pickup + browser verify từ run đầu; manual mail còn PLANNED |
| [x] | AUTH-REG-04 | PASS | 409 duplicate, account count vẫn 1 |
| [x] | AUTH-LOGIN-02 | PASS | Required validation |
| [x] | AUTH-LOGIN-03 | PASS | Wrong password 401 |
| [x] | AUTH-LOGIN-04 | PASS | Login success desktop/mobile |
| [x] | AUTH-LOGIN-05 | PASS | Reload session restore |
| [x] | AUTH-LOGIN-06 | PASS | Logout/guard |
| [x] | AUTH-ADMIN-01 | PASS | Normal user bị từ chối Admin |
| [x] | AUTH-ADMIN-02 | PASS | Temporary admin login |
| [x] | AUTH-ADMIN-03 | PASS | Admin logout/guard |

## 11. Binding và endpoint reconciliation

web-inventory.json là nghĩa vụ rà soát, không phải báo cáo coverage. Mỗi dòng
binding phải được mapping sang executable case ID. Có thể nhiều binding dùng chung
một case nếu case đó thực sự kích hoạt và assert từng binding; không được ghi
covered chỉ vì component render được.

### 11.1 Checklist mapping

- [ ] Mọi route trong inventory có parent scenario và case ID.
- [ ] Mọi event binding có step kích hoạt cụ thể và expected riêng.
- [ ] Mọi nhánh if, disabled, conditional template, tab, modal, drawer có
      ít nhất một case cho mỗi nhánh.
- [ ] Mọi enum option đã được chọn ít nhất một lần hoặc ghi N/A có lý do.
- [ ] Mọi navigation reference đã test target, query/path parameter và alias.
- [ ] Mọi form control có required/boundary/invalid/persistence phù hợp.
- [ ] Mọi API mutation có status, resource ID, duplicate/idempotency và restore.
- [ ] Dynamic route không dùng ID random không tồn tại trong checkpoint.
- [ ] Service side effect như mail, storage, FFmpeg, webhook, realtime có evidence
      riêng; không chỉ assert toast.
- [ ] Sau inventory regeneration, không còn row PLANNED bị bỏ quên ngoài scope.

### 11.2 Cách ghi mapping

Mỗi mapping nên có format:

~~~text
binding: frontend/<app>/<file>:<line>
event: click/change/submit/key/route
scenario: U-... hoặc A-... hoặc X-...
case: <executable-case-id>
actor: <actor>
expected: <assertion cụ thể>
evidence: <screenshot/report path>
status: PASS/REUSED/PLANNED/BLOCKED/FAIL
~~~

## 12. Release gate và Definition of Done

Không tuyên bố đã cover toàn bộ Web nếu còn một trong các điều kiện sau:

- [ ] Còn PLANNED, BLOCKED hoặc FAIL trong phạm vi release.
- [ ] Còn binding/route/navigation/form control chưa có case mapping.
- [ ] Còn parent flow PARTIAL ở channel permission, Studio nâng cao, watch
      state, admin mutation hoặc moderation SoD.
- [ ] Chưa chạy actor riêng cho Member, Reviewer 1, Reviewer 2 và Super Admin.
- [ ] Chưa có manual email screenshot cho các case có email contract.
- [ ] Chưa có đủ screenshot Desktop 1440×960, Mobile 390×844 và Tablet 768×1024
      cho từng case có UI.
- [ ] Chưa có persistence/API/resource ID assertion sau mutation.
- [ ] Chỉ có smoke route nhưng chưa assert button, modal, drawer, tab và enum.
- [ ] Payment webhook, recommendation service, R2/storage, FFmpeg hoặc mail
      dependency chưa được cung cấp nhưng vẫn ghi PASS.
- [ ] Responsive/keyboard/theme/language/performance chưa được chạy mà vẫn claim
      full UI coverage.
- [ ] Artifact run ID trong report không khớp run ID được ghi trong checklist.

### 12.1 Mẫu ghi một case mới

~~~text
## CASE: <ID>

- Status: [ ] PLANNED
- Parent scenario:
- Application / module / route:
- Actor:
- Run ID / tester / time:
- Precondition:
- Fixture/checkpoint/resource ID:
- Input:

### Steps
1.
2.
3.

### Expected
-

### Validation
- [ ] UI:
- [ ] API status/endpoint:
- [ ] DB/storage/resource ID:
- [ ] Reload/read-back:
- [ ] Negative/duplicate/retry:
- [ ] Permission/data isolation:
- [ ] Responsive/keyboard if applicable:

### Evidence
- [ ] Screenshot Desktop 1440×960:
- [ ] Screenshot Mobile 390×844:
- [ ] Screenshot Tablet 768×1024:
- [ ] Report:
- [ ] Mail manual:
- [ ] API/DB note:

### Observed

### Cleanup/restore

### Issue
~~~

## 13. Việc cần làm tiếp theo

1. Cập nhật run ID trong Section 0 sau khi xác nhận artifact pointer.
2. Tách actor thật cho Member, Reviewer 1, Reviewer 2, Admin và Super Admin.
3. Chạy manual mail evidence cho Section 3 trước khi đánh PASS auth/invitation/
   moderation email.
4. Hoàn tất các dòng PARTIAL theo thứ tự: channel permission → playlist/library
   → Studio upload/recovery → watch states → Admin mutation → moderation SoD.
5. Cung cấp payment sandbox/webhook và recommendation test service; nếu chưa có
   thì giữ BLOCKED.
6. Regenerate inventory, cập nhật binding mapping và chạy validator.
7. Chỉ sau khi toàn bộ gate ở Section 12 được tick mới chốt full coverage.
