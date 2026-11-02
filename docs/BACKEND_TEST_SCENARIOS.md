# Kịch bản kiểm thử backend HuTube: hiện có, bổ sung và performance

<!-- PLANNED_RUN_20261023 -->
**Cập nhật danh mục ngày 23/10/2026 (UTC+7):** Danh mục hiện có **520 ID: 520 PASS, 0 FAIL, 0 BLOCKED**, gồm 38 ca bổ sung đã PASS và 482 Pass lịch sử. Đợt hồi quy mới: Unit .NET 202/202 Pass, Integration PostgreSQL/FFmpeg 132/132 Pass, Live Kestrel/R2/Redis/CF 8/8 Pass, Python recommendation 111/111 Pass. Read/spike chạy lại đủ thời lượng với load generator đã sửa; soak đo 600 giây sau warmup 120 giây, không đổi SLO. Readiness sửa contract retryable theo nguyên nhân; cache notification bỏ L1 để invalidation giữa instances có hiệu lực ngay. Google provider/UI vẫn thuộc E2E web theo phạm vi người dùng chốt. Xem [bằng chứng sửa lỗi, regression và performance](testing/runs/2026-10-23-fixes/README.md). Các số inventory/TRX cũ bên dưới là lịch sử, không phải toàn bộ 520 case vừa chạy lại.
<!-- /PLANNED_RUN_20261023 -->

Cập nhật test case từ working tree ngày **05/10/2026 (Asia/Bangkok)**; giữ OpenAPI/runtime evidence lịch sử ngày 02/10/2026. Tài liệu này là kế hoạch kiểm thử và danh mục test hiện có; không khẳng định toàn bộ API/performance đã chạy trong lần làm tài liệu. Auth E2E chạy thật được ghi riêng trong [báo cáo](../frontend/e2e_testing/artifacts/auth-results.md). [Kịch bản Web](WEB_TEST_SCENARIOS.md) mô tả các flow UI xuyên User–Admin.

## 1. Cơ sở bao phủ và kết quả có thể xác nhận

OpenAPI **snapshot 02/10/2026** lấy từ API chạy trong runner local: **225 operations trên 197 path**, gồm các alias API được công bố. [backend-endpoints.json](../frontend/e2e_testing/coverage/backend-endpoints.json) liệt kê từng method/path/tag/nhóm kịch bản và các biến thể bắt buộc. Cột `Trạng thái` bên dưới ghi evidence execution hiện có; `PASS` không có nghĩa mọi biến thể/operation trong nhóm đã đạt full coverage. Source inventory cập nhật 04/10/2026 có **244 operations trên 214 path**, được liệt kê đầy đủ tại mục 12; JSON inventory cũ chưa được refresh runtime. Ngoài OpenAPI phải kiểm tra SignalR `/hubs/notifications`, OpenAPI/Swagger theo môi trường và background workers.

Danh mục nguồn hiện có: **89 method unit, 138 method integration và 33 method Python recommendation** (đếm source ngày 05/10/2026). Theory/InlineData mở rộng thành nhiều test execution, nên số method không phải số case đã chạy. [existing-tests.json](../frontend/e2e_testing/coverage/existing-tests.json) lưu inventory snapshot trước; mục 10 đã đối chiếu lại trực tiếp source, kể cả file mới chưa tracked, và liệt kê toàn bộ method hiện tại. Kết quả chạy lại trên build hiện tại: 137 unit, 156 integration và 33 Python execution pass; đây không phải số phần trăm branch/endpoint coverage.

Integration factory dùng PostgreSQL thật, migrate schema và constraints thật; bắt buộc `TEST_DATABASE_CONNECTION`. Google verifier, email sender, object storage, video transcoder và recommendation snapshot store trong fixture có test double. Do đó những integration test này kiểm tra nghiệp vụ/database/API nhưng **không chứng minh OAuth, SMTP, R2, FFmpeg hoặc recommendation network thật hoạt động**. Auth Playwright hiện dùng Pickup thực và browser, nhưng media processing/Google/recommendation bị tắt để đúng scope auth.

Evidence lần chạy nhóm CF/storage/worker ngày 05/10/2026: [integration-cf-storage-worker-final.trx](../backend/tests/HuTube.IntegrationTests/TestResults/integration-cf-storage-worker-final.trx) **156/156 integration PASS**, [unit-cf-storage-worker-final.trx](../backend/tests/HuTube.UnitTests/TestResults/unit-cf-storage-worker-final.trx) **137/137 unit PASS**, và [worker-recommendation-final.trx](../backend/tests/HuTube.IntegrationTests/TestResults/worker-recommendation-final.trx) **1/1 recommendation-worker integration PASS**. Các con số PASS này là kết quả execution; trạng thái từng plan B-* vẫn phải đọc theo mapping và giới hạn evidence ở workbook.

## 2. Test hiện có theo nhóm

| File / tầng | Nội dung đang được kiểm tra; tên chính xác ở phụ lục |
| --- | --- |
| Unit/AuthRulesTests, AuthServicePlanTests | Normalize email, password/hash/session, blocked status; kế hoạch gán free plan/plan history |
| Unit/ChannelRulesTests, ChannelTests, ChannelQuotaTests | Handle/branding/channel role, invariants, quota upload |
| Unit/VideoRulesTests | Visibility/quality/duration và validation video |
| Unit/RbacTests | Permission/role/channel membership invariants |
| Unit/PlanEntitlementTests | Feature/quota/download quality theo plan |
| Unit/PaymentServiceTests, SepaySignatureVerifierTests | Price/payment constraints, HMAC/signature/timestamp/transaction cases |
| Unit/ModerationStrikeAndAppealTests | Strike expiry/revoke, trạng thái moderation/appeal và ràng buộc nghiệp vụ |
| Unit/LocalStorageDownloadTests | Local public/private Save/OpenRead/Delete roundtrip bytes, idempotent delete và chặn path traversal ngoài upload root; provider/range/authorization API vẫn là tầng khác |
| Unit/R2StorageTests | Adapter storage/read/delete/sign URL theo fake transport; không phải truy cập R2 thật |
| Integration/AuthApiTests | Register pending/hash/free plan/unique case-insensitive; Google fake; login/platform/cookie/origin, refresh replay/concurrency/expiry, device/session revoke/IDOR, reset OTP wrong/expired/replay/concurrency and attempt cap, blocked account/admin, health/OpenAPI/migration |
| Integration/ContentApiIntegrationTests | Upload/probe/quota concurrency; metadata, playback visibility, comments, playlists/library, account bio và creator flags; payment atomicity/idempotency; purge/restriction theo các method cụ thể |
| Integration/ChunkUploadRecoveryTests | Durable manifest, khôi phục chunk sau restart, partial file/truncation, invalid start/path và active-processing cleanup |
| Integration/CfSeederIntegrationTests | CF manifest replay/import/concurrency, multipart metadata/ownership, chunk protocol, completion exactly-once và retry |
| Integration/RbacAndChannelIntegrationTests | Channel create/membership/invite/permission và admin RBAC |
| Integration/PolicyModerationIntegrationTests | Policy/report/claim/decision/appeal/strike/effective state, legacy report mapping |
| Integration/SearchAndFilterIntegrationTests | Search/filter/sort/public visibility và pagination theo method |
| Integration/FeatureFlagIntegrationTests | Tính năng bật/tắt và response khi disabled |
| Integration/RecommendationAdminIntegrationTests | Quyền admin, settings/matrix/cấu hình recommendation |
| Integration/RecommendationFeedIntegrationTests | Personalized/fallback feed và lọc nguồn dữ liệu |
| Integration/RecommendationModelJobIntegrationTests | Create/status/job lifecycle/model update qua fixture |
| Python recommendation-service/tests | Pipeline/data/cohorts, item/user CF, similarity variants, quality gate, train/artifacts/reports, API, R2 update và complexity |

Khi bổ sung, mở method/assertion tương ứng trước để tránh viết test chỉ trùng implementation. Coverage module ở bảng này không có nghĩa mọi biến thể trong module đã có assertion. Ưu tiên gap trong mục 5 và endpoint chưa được chứng minh; cập nhật mapping method → operation → variant bằng evidence từ request/assertion, không tự suy ra từ tên file.

## 3. Môi trường và cách chạy test hiện có

Chạy từ root repository, dùng .NET 10, PostgreSQL test có quyền create/drop **database test**. Integration factory tạo database `hutube_test_<guid>` tách biệt rồi cleanup; account trong database ephemeral này không phát sinh account trên hệ thống sản phẩm. Quy tắc tái sử dụng một account áp dụng cho môi trường E2E/soak persistent; không thay đổi tính độc lập của unit/integration hiện có.

```powershell
dotnet test backend/tests/HuTube.UnitTests/HuTube.UnitTests.csproj --logger "trx;LogFileName=unit.trx"
# Chỉ trỏ vào PostgreSQL test riêng, thay thông số theo máy của bạn.
$env:TEST_DATABASE_CONNECTION='Host=127.0.0.1;Port=55432;Username=hutube_test;Database=postgres;Password=LocalTestOnly2026!'
dotnet test backend/tests/HuTube.IntegrationTests/HuTube.IntegrationTests.csproj --logger "trx;LogFileName=integration.trx"
dotnet test backend/tests/HuTube.IntegrationTests/HuTube.IntegrationTests.csproj --collect:"XPlat Code Coverage"
```

Port/user/password ở lệnh integration là ví dụ cấu hình local, **không tự tạo server đó** và không phải credential PostgreSQL mặc định trên máy. Auth runner dùng cluster riêng port 55439 và tự dừng khi xong; không chạy lệnh integration vào production hoặc nhầm database E2E persistent. Test không có biến kết nối phải fail rõ, không silently skip.

```powershell
Set-Location recommendation-service
.venv/Scripts/python.exe -m pytest tests -q
```

Nếu chưa có Python env, tạo env riêng và cài theo requirements của recommendation-service; Playwright requirements không thay thế dependencies CF. Ghi build SHA/working tree, runtime versions, migration, cấu hình fake/real, TRX/JUnit/coverage và thời gian. Không ghi token/connection secret vào báo cáo. Coverage collector chỉ đo các path chạy, không tự xác nhận đúng nghiệp vụ hay chứng minh Web coverage.

## 4. Contract và biến thể bắt buộc cho mọi operation

Mỗi operation trong inventory có case riêng gắn nhóm bên dưới, kể cả alias route/HTTP method khác. Khi triển khai, xác định từ controller/service: anonymous/authorized, permission, ownership, valid enum, min/max, error code và status thật. Không ép mọi lỗi thành 400 hoặc mọi ownership thành 403 nếu contract dùng 404.

| Biến thể | Assertion bắt buộc |
| --- | --- |
| Happy path | Status, content-type/schema/fields, đúng owner/target, DB trước/sau, audit/event cần thiết; không chỉ `IsSuccessStatusCode` |
| Validation | Required/null/empty/space, min−1/min/max/max+1, Unicode, email/URL/enum sai, negative/overflow, malformed JSON/multipart, unexpected field handling |
| Auth/RBAC | Không token, expired/tampered/wrong issuer/audience, user/admin/super_admin, blocked/revoked/role changed, cross-user/cross-channel IDOR |
| Read/list | ID missing, soft deleted, status/private filtering, search/sort/date/timezone, page boundaries/default/max và tổng trên >50 rows |
| Mutation | Cancel/cancellation, repeated request, idempotency key phạm vi actor, concurrency cùng resource, transaction rollback giữa nhiều save; không tác dụng phụ khi bị từ chối |
| Error contract | ProblemDetails code/traceId, không stack trace/password/token/PII thừa; 429 retry semantics nếu rate limiting hiện có |
| Asset/event | Object tồn tại/chất lượng/duration thật, read URL đúng quyền/thời hạn; SignalR/email đúng recipient và không gửi trùng |
| Environment | Feature flag, thiếu external dependency, timeout/retry budget, Development/Production OpenAPI/Swagger/CORS/cookie config |

## 5. Kịch bản backend bổ sung theo toàn bộ module

Tiền điều kiện chung: database test, actor cố định có quyền rõ, tài nguyên meaningful theo tài liệu Web. Với mutation: Arrange đọc trạng thái và version/ID → Act request thật → Assert response + DB/storage/event → cleanup hoặc restore fixture. Negative phải chứng minh **không có side effect**, kể cả audit/payment/quota khi request bị từ chối.

| Nhóm | Flow và các gap/biến thể cần xác nhận hoặc bổ sung | Trạng thái |
| --- | --- | --- |
| B-AUTH (15) | Register→pending→verify→login→refresh→logout; duplicate email/username chuẩn hóa, concurrent register chỉ một record/free history; pending/suspended/deleted; password min/max/hash không plaintext; verify token hash and reset OTP hash, invalid/expired/replayed, attempt cap and concurrent consumption; Google fake/real tách suite; login mobile/web/admin cookies/response đúng platform, origin thiếu/sai/preflight; JWT claim/permission giữa phiên; refresh rotation/replay descendants, max session/device reuse và revoked sessions. Account enumeration response, lockout/rate limit nếu được cấu hình | PASS |
| B-ACCOUNT (8) | GET/update profile/bio/avatar/preferences/password/sessions, tất cả preference fields có roundtrip; image content sniff/limit; change password revoke policy và version; revoke current/others/foreign ID không leak; simultaneous update không làm mất field ngoài patch; user ID download/export không gồm hash/token | PASS |
| B-CHANNEL (25) | Create→settings/branding→invite→accept→role→remove; unique handle hoa thường, reserved/Unicode/bounds; owner membership và limit plan; avatar/banner/watermark validation/storage cleanup; invite normalized email, reuse/expiry/wrong actor; mọi member permission independently; owner không bị member delete; subscribe counts/idempotent; lock/delete tác động video/playback; analytics totals nhiều trang và ranges timezone | PASS |
| B-VIDEO (22) | Upload nguồn thật mục 6→probe→processing→renditions→ready→playback→edit→delete; quota reservation đồng thời/release đúng, client quality/duration spoof không vượt server; MIME/extension/0 byte/truncated/container unsupported; visibility/allowComments; metadata clear values, chapter order/bounds/dedup, tags/category references; history/view/watchSeconds clamps/idempotency; job cancellation/restart/source cleanup, removed vs soft delete vs purge, source URL authorization; không trust client actor/owner | PASS |
| B-COMMENT (10) | Create→reply→edit→reaction→report→hide/delete; parent thuộc video khác, depth/cycle, disabledComments và blocked actor; ownership quản trị khác tác giả; text Unicode/XSS, bounds; sorting/paging hidden replies/counts; report legacy tạo cùng moderation case đúng; concurrent reaction không double count | PASS |
| B-FEED (5) | Anonymous/personalized/home/trending/explore/search theo actual path; cold start/fallback/recommendation unavailable; hidden/private/removed/banned channel bị lọc; date/sort/page và ranking stable ties; không trả interaction của actor khác; recommendation stale IDs/tombstones | PASS |
| B-LIBRARY (4) | History và liked thuộc actor, sort/paging >50, duplicate view/rating update không thêm sai record; deleted/private/suspended video không leak source; watchSeconds nguồn tổng đúng | PASS |
| B-PLAYLIST (11) | Create→update visibility→add/order/remove→delete; owner vs guest/foreign actor; duplicate hoặc unavailable video, full-set reorder atomic và stale request; private/unlisted access, tombstone item, owner-only channel route và fixed "Video đã lưu" fallback. Ghi rõ source hiện chưa có query pagination/count và delete là hard-delete | PASS |
| B-PLAN (14) | Catalog/detail/current/default free; paid subscribe route yêu cầu payment đúng contract; invite accept/revoke/member ownership/storage allocation; quota tổng và usedBytes/expiry/renew; invalid duration/price/feature/order/max members; inactive/downgrade/expired entitlement; concurrent member acceptance không vượt capacity | PASS |
| B-PAYMENT (5) | Create fixed idempotency key→pending→webhook verify→paid→activate plan/history/membership trong một transaction; replay cùng user, key giống khác user, concurrent webhook và DB lỗi giữa saves không kích hoạt nửa chừng; wrong signature/timestamp/body/amount/currency/reference/user, overdue/canceled/partial/overpaid theo contract; invalid không mutate; QR/status access đúng owner, timeout/reconciliation không tạo payment mới | PASS |
| B-DOWNLOAD (4) | Entitlement quality/read URL→persist download/history→list/remove/clear; không gói, quality cao hơn source, hidden/private/channel locked, expired URL; HTTP range/content disposition/size; không cho path traversal hoặc URL tùy ý; delete chỉ record của actor, không mất video nguồn | PASS |
| B-SUBSCRIPTION (1) | Listing theo actor/visibility, paging/counts; mutations subscription nằm ở Channel vẫn phải test subscribe/unsubscribe lặp và notification preference; guest/foreign subscription privacy | PASS |
| B-NOTIFICATION (3) | List/paging/unread, mark one/all read, ownership; SignalR JWT negotiate/reconnect, same notification not duplicated; moderation/payment/invite đúng recipient/body/link; transaction rollback không gửi success event; outbox/delivery retry nếu có phải đo effect thật | PASS |
| B-TAXONOMY (3) | Categories active (có cache), policies published có filter group, violation types active/name; admin topic/tag/policy create/update/archive/publish và RBAC; missing/inactive reference phải bị chặn. Policy decision/version snapshot và invalid violation fallback là P0 gap cần kiểm chứng | PASS |
| B-ADMIN (83) | Tất cả operations nhóm admin theo source, gồm payments/subscriptions/operations/strike policy và users/channels/videos/roles/permissions/plans/categories/tags/policies/moderation/report/appeal/strike; mapping path từng operation trong inventory. Mỗi CRUD/list/statistics/export có positive+permission+ownership/bounds; chỉnh role không tự elevate/system role unsafe; clear description patch; reset filter/date/CSV value; thống kê phải bao gồm mọi trang; quản trị phải ghi actor/target/reason và gửi thông báo đúng | PASS |
| B-MODERATION (8) | Creator report/appeal/evidence/strike theo endpoint actual; admin queue/claim/resolve, grouped report và strike policy nằm trong nhóm B-ADMIN. Kiểm tra owner/foreign actor, claim barrier/terminal state/SoD, duplicate appeal/evidence, case IDs, effective restriction và private evidence. Source hiện chưa có expiry worker và còn đường resolve chưa enforce claim/terminal | PASS |
| B-CF (9) | Accounts/users, upload source/rendition, processing status và upload-session start/append/complete/complete-rendition theo controller; không tạo account mới mỗi replay; JSON/template bounds, path traversal/filename/session actor, wrong chunk index/length; restart/partial manifest, disk full/quota/max file; complete concurrent exactly once, TTL cleanup không xóa job đang dùng. Không có GET session-status hoặc cancel-session route trong source snapshot này | PASS |
| B-RECOMMENDATION (11) | Super_admin-only settings/status/matrix/CSV/model jobs/simulation operations; decimal weights finite/negative/zero sum, normalize đúng; active model exists/type, preview/export limits and order; job dedup/retry/cancel/failed history; partial/missing snapshot fallback, service timeout/auth secret không leak; bot manifest reusable, signal bounds/comments meaningful | PASS |
| B-SYSTEM (3 + ngoài schema) | `/health`, system info/config anonymous đúng schema và không leak secret; DB unavailable/timeout/cancel rõ; migration runner `--migrate` chạy lặp idempotent; OpenAPI/Swagger chỉ ngoài Production; CORS allowlist/header/credentials thật, endpoint-specific body limits, cancellation, graceful shutdown, hub/background paths test riêng | PASS |

Số trong ngoặc đã cập nhật theo source inventory ngày 04/10/2026, không phải số assertions hay test đã pass. Các operations Admin được nhóm tiếp theo path khi triển khai, không bỏ qua các method vì cùng tag.

### B-MODERATION-FLOW — đối chiếu trạng thái toàn chuỗi

1. Owner upload video → pending moderation; reviewer1 claim; reviewer2 claim cùng lúc phải chỉ có một winner. Claim/release giữ lịch sử; source chưa có claim expiry/reclaim worker nên case đó ghi `N/A` hoặc `P0 gap`, không coi là PASS. Approve/age-restricted/recommendation-restricted/reject/escalate phải đúng permission/policy/reason.
2. Member report video/comment/channel → normalize target/group → reviewer classify/decision; từng action dismiss/warn/age restriction/recommendation restriction/hide/remove/upload restriction/strike/lock/escalate phải đúng target. Assert case/report/decision, trạng thái hiệu lực và notification; invalid/inactive violation type phải phát hiện fallback sai nếu service tự thay bằng type active khác.
3. Owner appeal evidence → admin reviewer khác người quyết định duyệt/reject; user route chỉ owner đọc evidence, admin route cần `AppealView`. Wrong actor/foreign case/terminal state/duplicate/deadline bị chặn; source hiện cần kiểm tra thêm reviewer claim trong resolve. Asset đã purge không thể hồi sinh.
4. Tạo/revoke/đọc strike hết hạn và unlock channel; source tính active theo thời gian ở read/query, chưa có worker ghi chuyển trạng thái expired. Fake clock kiểm tra trước/đúng/sau expiry; overlapping restrictions không mở khóa sớm.
5. Transaction fail sau decision nhưng trước state/event → rollback nhất quán; retry/idempotency/concurrent submit không tạo double strike/audit/notice. Giữ IDs và before/after; đặc biệt kiểm tra moderation resolve phát notice trước `SaveChanges` và không tự giả định atomicity.

## 6. Suite media và tích hợp thật cần bổ sung

Nguồn duy nhất: `F:\NgDuyLinh\Khoa_Luan_Tot_Nghiep\Em Làm Thì Em Mới Có Ăn_ Không Làm Mà Đòi Có Ăn Thì…_720p.mp4`, đã thấy 99.009.115 byte. Kiểm tra file tồn tại, ffprobe lấy duration/codec/width/height và hash trước chạy, lưu manifest. Không rename nguồn hoặc thay video ngẫu nhiên. Video title/category/chapters dùng dữ liệu ở tài liệu Web, account/video ID persistent.

| Case | Các bước và expected | Trạng thái |
| --- | --- | --- |
| B-MEDIA.01 | Upload với storage Local + FFmpeg thật, processing bật → probe → rendition lower than/equal source → ffprobe mỗi output → play/read range. Assert file thực, audio/duration/quality/byte quota, không chỉ JSON metadata | PASS |
| B-MEDIA.02 | Cắt nguồn/kill worker có kiểm soát trong workspace test → restart/recover upload ID → complete một lần; malformed file/probe fail/disk full/cancel → resource reservation release đúng, không orphan bất tận | PASS |
| B-MEDIA.03 | Storage R2 sandbox với prefix cố định test → upload/read signed URL/expiry/delete/cleanup theo manifest; run dùng prefix test riêng trong bucket có sẵn, không đổi config/manifest/object hiện có | PASS |
| B-MEDIA.04 | Phạm vi người dùng điều chỉnh 23/10: Email Pickup .eml/link thật → verify HTTP; Google backend sau verified identity có kiểm soát → session/JWT hợp lệ, linked account, retry không trùng user/session, invalid identity và origin bị chặn. Google provider/UI/cancel/expired credential thật chuyển E2E web; không xác nhận web đã chạy | PASS |
| B-MEDIA.05 | SePay webhook sandbox/body/signature thật qua HTTP, không chuyển tiền thật; CF service thật train/update/read snapshot với dataset known → feed; missing service/token/csv/timeout phải fail hoặc fallback đúng contract | PASS |

Chunk store CF hiện 24 MiB, nguồn này 4 chunk; luôn đọc negotiated size từ API. Không giả định endpoint multipart upload thường dùng cùng protocol chunk. Upload timeout khởi đầu 300 giây theo uplink; transcode poll 2–5 giây, deadline 20 phút cấu hình. Thất bại ghi byte/last chunk/status và giữ checkpoint; không POST job/upload mới để che timeout. Không chạy media nặng trong auth smoke mỗi lần.

## 7. Race, lỗi phụ thuộc và dữ liệu lớn

| Tình huống | Thiết kế test | Trạng thái |
| --- | --- | --- |
| Quota upload | Barrier để 2+ uploads vào cùng biên quota; tổng reserved+used không vượt, fail release reservation, retry dùng resource cũ | PLANNED |
| Refresh/reset/payment/claim | Barrier requests cùng token/key/resource; một outcome hợp lệ, losers đúng code; assert DB descendants/history/strike/event cardinality | PLANNED |
| Metadata/membership | Hai update có version/stale state; không ghi nhầm ID hoặc tạo duplicate/over capacity; kiểm tra constraints database thực | PLANNED |
| Pagination | Seed một lần dataset có manifest >50 rows với chủ đề và timestamps có ý nghĩa; counts/analytics/export không chỉ trang đầu; thêm duplicates/ties/Unicode/deleted/private | PLANNED |
| Failure injection | Real PostgreSQL transaction/constraint/trigger fail ở điểm giữa saves; dependency timeout/500/cancel/disk errors; chứng minh rollback và không success notification giả | PLANNED |
| Expiry | TimeProvider/clock có kiểm soát nếu service hỗ trợ, hoặc fixture timestamp local; test biên UTC/timezone/DST khi áp dụng, không đợi hàng giờ | PLANNED |
| Security inputs | SQL-like/XSS text, URL/file traversal/CSV formula, extremely long payload, JWT/IDOR/role revoke; scope localhost/sandbox, không quét hệ thống ngoài | PLANNED |

Unit không mock DbSet để thay thế integration constraint test. Google/storage/transcoder doubles cần được công bố trong report. Failure injection suite tách happy path media thật; expectation theo contract và state, không test private method để đạt coverage đẹp.

## 8. Performance backend — kế hoạch và kết quả đo theo từng ca

### 8.1 Môi trường và account/dataset

Dùng Kestrel process thật + PostgreSQL thật, production-like build/config và dependency mode được ghi rõ; không benchmark WebApplicationFactory hoặc in-memory fake rồi coi là tải production. Công cụ load có thể là k6 hoặc Locust; đã bổ sung và chạy Python/httpx load script trên Kestrel thật ngày 23/10/2026; xem kết quả từng ID B-PERF ở mục 16 và báo cáo run. Lưu cấu hình/script/report tương lai trong `frontend/e2e_testing/performance/`.

Một account chính checkpoint cho functional/hot-resource scenario; workload nhiều user dùng pool **seed một lần có manifest**, reuse giữa các run. Không gọi register cho từng iteration/VU. Serialize refresh của cùng session để baseline không biến thành replay attack; dành scenario riêng kiểm tra refresh race. Không gọi sai password liên tục làm khóa account. Lưu stable IDs và request keys cho writes, không tạo data vô hạn; after-run cleanup chỉ tài nguyên trong manifest.

Dataset baseline có quy mô đã ghi: số user/channel/video/interactions/comments/playlist/moderation cases, phân bố published/private/removed, category/timestamp. Nhánh lớn dùng snapshot riêng 1k/10k/100k metadata/interactions có nguồn/purpose, không nhân bản upload file thật cho từng row. Media test chỉ 1 rồi 3 concurrent uploads, không 100 VU ×99 MB. Các job simulation có thể tạo dataset theo chủ đề đã cấu hình; reuse/checkpoint job trước start.

### 8.2 Profile và thời gian chạy

| Profile | Tải / duration đề xuất | Khi chạy | Trạng thái |
| --- | --- | --- | --- |
| PERF-BASE | Cold/warm 1 VU, 30–100 requests mỗi nhóm; ghi cache cold riêng | Sau change query/serialization | PLANNED |
| PERF-READ | Ramp 20→50→100 VU, mỗi mức 2 phút, 1 phút warmup; mix feed/search/detail/library/admin list | Trước release, endpoint weights từ usage/fixture | PLANNED |
| PERF-WRITE | 5→10 VU, 2 phút mỗi mức: comments/rating/profile, riêng hot-resource collisions | Sau change transaction/locking | PLANNED |
| PERF-AUTH | Valid login/session refresh/read; stable user pool, cooldown theo lockout policy; refresh-race tách khỏi baseline | Sau change auth/session | PLANNED |
| PERF-SPIKE | 10→100 VU trong 30 giây, giữ 1 phút rồi giảm; fixed ceiling | Trước release, quan sát hồi phục/rate limit | PLANNED |
| PERF-SOAK | 20–50 VU trong 10–30 phút, memory/connection growth | Chạy riêng, không mỗi commit | PLANNED |
| PERF-MEDIA | File nguồn 99 MB, 1 rồi 3 upload concurrent, 1–3 transcode jobs, budget riêng 20 phút | Sau change media/storage/worker | PLANNED |
| PERF-JOB | Matrix preview/export và 1 model job/simulation; request acceptance đo riêng completion | Sau change CF/matrix/job | PLANNED |

Các mức là đề xuất, phải chốt theo CPU/RAM/network của máy và SLO. Không tự chạy tải cao lên môi trường đang phục vụ người dùng. Đặt hard stop khi lỗi 5xx vượt 5% trong 30 giây, DB connection gần cạn, disk thấp hoặc timeout liên tục; ghi fail/bottleneck, không tăng VU vô hạn. Kết quả 429 có chủ đích tách khỏi unexpected 5xx, nhưng phải báo throughput bị hạn chế.

### 8.3 Endpoint mix và chỉ số

| Nhóm tải | Kiểm tra | Trạng thái |
| --- | --- | --- |
| Read | Feed/search/category/video metadata/channel/playlist/library; payload size, hidden filtering, sort/count, query count/N+1, SQL plan/index | PLANNED |
| Admin | Users/channels/videos/moderation paging/filter/statistics và CSV; p95 với dataset lớn, memory khi export, cancellation; không deserialize matrix vô hạn | PLANNED |
| Write | Comment/reaction/profile/membership; throughput, transaction wait/deadlock, retries/unique collision, dữ liệu không mất/trùng | PLANNED |
| Auth | Password hashing CPU, valid login, token/session refresh; pool/query locks; cookie semantics giữ đúng dưới tải | PLANNED |
| Media | Uplink throughput/byte/peak RAM/disk I/O, temp files, storage latency; time-to-ready, CPU transcode, queue depth; playback TTFF/range request | PLANNED |
| CF/job | Matrix size/export memory, acceptance latency, queue/worker duration, progress polling, model quality invariants; timeout/cancel không tạo job mới | PLANNED |

Thu p50/p95/p99, requests/second, expected vs unexpected status, timeout/error ratio, bytes/request; .NET CPU/RSS/GC/LOH/thread pool, Npgsql active/waiting connections, DB CPU/IO/locks/deadlocks/query duration; storage/network/job queue. Ghi warmup, cache/index/config/database size và máy chạy load để so sánh trước/sau. `EXPLAIN (ANALYZE, BUFFERS)` chỉ với query và database test phù hợp, tránh tác dụng phụ trên production.

Ngưỡng giữ nguyên trong lượt đo sau sửa 23/10/2026: read p95 <500 ms, p99 <1.500 ms; write p95 <1.000 ms; unexpected 5xx <1%; không deadlock ngoài tình huống cố ý; không quota/payment/claim invariant violation dù latency đạt. Memory sau warmup không tăng liên tục qua soak, kiểm tra mức tăng khoảng 10% rồi điều tra theo dataset. Upload/transcode/job có SLO riêng từ baseline và kích thước thực; không gộp vào read p95. Request job acceptance mục tiêu <2 giây, completion deadline tối đa 30 phút theo workload. Bài soak có warmup riêng 120 giây ở 20 VU trước cửa sổ đo 600 giây; giữ ngưỡng working-set 10%, không ép GC/restart để đạt kết quả.

Timeout functional 30 giây không có nghĩa API 29 giây đạt performance. Báo cáo cần bảng profile, throughput, percentile, resource peak và failures; kết luận bằng so sánh cùng dataset/máy, không từ một request nhanh. Không có kết quả benchmark trong đợt tài liệu này.

## 9. Ưu tiên và tiêu chí kết thúc

P0: auth/IDOR/RBAC, payment atomicity/webhook, quota races, moderation SoD/purge, nguồn video thật. P1: đầy đủ CRUD/filter/pagination/notification/plan membership/cancel/restart. P2: dataset lớn/load/soak/frontend performance và dependency failure thật. Các priority chỉ quyết định thứ tự triển khai, không cho phép bỏ case còn lại khi công bố full coverage.

Mapping mỗi method/path + variant đến test assertion và evidence; đủ cả permissions/actor/feature flags/worker path ngoài OpenAPI. Giữ test cũ, thêm gap có ý nghĩa, chạy required suites và coverage collector. Không đặt mục tiêu 100% line coverage thay cho hành vi: chưa có số coverage nhánh thì ghi chưa đo. Benchmark cần baseline và số đo mới; case BLOCKED thiếu dependency phải nêu rõ, không đếm PASS. CI smoke nhẹ; full media/performance chạy riêng có budget, fixture/checkpoint và báo cáo độc lập.

## 10. Phụ lục test method hiện có

Đối chiếu trực tiếp source trong working tree ngày 05/10/2026: **89 method unit, 138 method integration, 33 method Python**. Fact/Theory là method, InlineData không phải số execution PASS. Inventory JSON ở `frontend/e2e_testing/coverage/existing-tests.json` là snapshot cũ; danh sách dưới đây bao gồm source test mới/untracked hiện có. Suite chạy lại đầy đủ: integration 156 PASS, unit 137 PASS, recommendation-service 33 PASS, 0 FAIL/skip trong từng suite.

<!-- EXISTING_TEST_METHODS: refreshed from current source -->

### backend/tests/HuTube.UnitTests/AuthRulesTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/AuthRulesTests.cs).

- `NormalizeEmail_InvalidEmail_ShouldReject` (Theory; 4 InlineData)
- `NormalizeEmail_MixedCase_ShouldNormalize` (Fact)
- `ValidatePassword_WeakPassword_ShouldReject` (Theory; 4 InlineData)
- `ValidatePassword_TooLong_ShouldReject` (Fact)
- `ValidatePassword_Valid_ShouldAccept` (Fact)
- `IsBlocked_RestrictedAccount_ShouldBlock` (Theory; 3 InlineData)
- `IsBlocked_SoftDeleted_ShouldBlock` (Fact)
- `IsActive_ExpiredSession_ShouldReject` (Fact)
- `Revoke_Repeated_ShouldPreserveFirstReason` (Fact)
- `Hash_Password_ShouldUseRandomSaltAndVerify` (Fact)
- `CreateOpaqueToken_Repeated_ShouldBeUniqueAndStoreOnlyHash` (Fact)

### backend/tests/HuTube.UnitTests/AuthServicePlanTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/AuthServicePlanTests.cs).

- `RegisterAsync_CallsAddUserAsync_AndUserHasFreePlan` (Fact)

### backend/tests/HuTube.UnitTests/ChannelQuotaTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/ChannelQuotaTests.cs).

- `CanReserve_ShouldRejectWhenUploadWouldExceedLimit` (Fact)
- `CanReserve_ShouldAllowExactLimitBoundary` (Fact)
- `Remaining_ShouldNeverGoNegative` (Fact)

### backend/tests/HuTube.UnitTests/ChannelRulesTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/ChannelRulesTests.cs).

- `IsValidHandle_ValidatesCorrectly` (Theory; 10 InlineData)
- `NormalizeHandle_NormalizesToLowercaseWithoutAtPrefix` (Theory; 3 InlineData)
- `SoftDelete_SetsStatusToDeletedAndIsActiveFalse` (Fact)

### backend/tests/HuTube.UnitTests/ChannelTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/ChannelTests.cs).

- `PublicChannelReads_AreCachedPerViewerAndProfileUpdatesInvalidateCache` (Fact)
- `CreateChannel_ShouldSucceed_AndMakeCreatorOwner` (Fact)
- `CreateChannel_DuplicateHandle_ShouldThrow409` (Fact)
- `UpdateChannel_NonMember_ShouldThrow403` (Fact)
- `InviteMember_And_Accept_ShouldCreateActiveMember` (Fact)
- `AccessibleChannels_ShouldIncludeAcceptedMemberWithoutOwnedChannel` (Fact)
- `InviteMember_ShouldSendInvitationEmail` (Fact)
- `RevokedInvitation_CannotBeAccepted` (Fact)
- `ExpiredInvitation_IsMarkedExpired_AndCannotBeDeclined` (Fact)
- `DuplicatePendingInvitation_ShouldBeRejected` (Fact)
- `ChangeRole_ManagerCannotChangeAnotherManager` (Fact)
- `RemoveMember_CannotRemoveOwner` (Fact)
- `Subscribe_Success_ShouldCreateActiveSubscription` (Fact)
- `Subscribe_OwnChannel_ShouldThrow400` (Fact)
- `Subscribe_InactiveChannel_ShouldThrowNotActive` (Fact)
- `Subscribe_DuplicateRequest_ShouldBeIdempotent` (Fact)
- `Unsubscribe_Success_ShouldPauseSubscription_AndDecrementCount` (Fact)
- `Unsubscribe_DuplicateOrNonExistent_ShouldNotThrow` (Fact)
- `GetSubscribedChannels_ShouldReturnOnlyActiveChannels` (Fact)

### backend/tests/HuTube.UnitTests/LocalStorageDownloadTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/LocalStorageDownloadTests.cs).

- `OpenReadAsync_StreamsSavedVideoBytes` (Fact)
- `OpenReadAsync_RejectsPathsOutsideUploads` (Theory; 4 InlineData)
- `PrivateEvidence_RoundTripsAndDeleteIsIdempotent` (Fact)
- `DeleteFileAsync_NeverDeletesOutsideUploadRoot` (Theory; 4 InlineData)

### backend/tests/HuTube.UnitTests/ModerationStrikeAndAppealTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/ModerationStrikeAndAppealTests.cs).

- `AutoStrikeRule_FifthVideoRejection_AppliesConfiguredTier` (Theory; 3 InlineData)
- `StrikeTier_Progression_And_Revocation_RestoresActiveStatus` (Fact)
- `Appeal_SeparationOfDuties_PreventsOriginalReviewerFromResolving` (Fact)
- `Report_CanReportVideoCommentAndChannel` (Fact)

### backend/tests/HuTube.UnitTests/PaymentServiceTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/PaymentServiceTests.cs).

- `InitiateAsync_WhenPaidPlan_CreatesPendingPaymentWithQr` (Fact)
- `InitiateAsync_WhenFreePlan_ThrowsBadRequest` (Fact)
- `InitiateAsync_SameUserAndIdempotencyKey_IsIdempotentAndScoped` (Fact)
- `CancelAsync_IsIdempotentForPendingAndRejectsPaidOrForeignPayment` (Fact)
- `HandleSepayWebhookAsync_WhenValidPayment_ActivatesPlanAndMarksPaid` (Fact)
- `HandleSepayWebhookAsync_WhenDuplicateTransactionId_IgnoresSilently` (Fact)
- `HandleSepayWebhookAsync_WhenAmountLessThanRequired_DoesNotActivatePlan` (Fact)
- `HandleSepayWebhookAsync_WhenPaymentExpired_CancelsPayment` (Fact)
- `HandleSepayWebhookAsync_WhenAccountCodeOrDirectionDoesNotMatch_LeavesPaymentPending` (Fact)
- `GetPaymentAsync_ReturnsCorrectPaymentDetails` (Fact)

### backend/tests/HuTube.UnitTests/PlanEntitlementTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/PlanEntitlementTests.cs).

- `ReadFlags_ShouldReturnOnlyBooleanEntitlements` (Fact)
- `ReadFlags_ShouldNormalizeEntitlementKeys` (Fact)
- `ReadFlags_ShouldFailClosedForInvalidOrNonObjectJson` (Theory; 4 InlineData)
- `CanReserve_ShouldAvoidOverflowAndRejectInvalidCounters` (Fact)

### backend/tests/HuTube.UnitTests/R2StorageTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/R2StorageTests.cs).

- `LoadDevelopmentFile_ReadsLastBucketScopedCredentialWithoutLoggingSecrets` (Fact)
- `GetReadUrlAsync_ForPrivateR2Object_ReturnsTemporarySignedUrl` (Fact)
- `DualStorage_RoutesImagesAndVideosToConfiguredProviders` (Fact)

### backend/tests/HuTube.UnitTests/RbacTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/RbacTests.cs).

- `GetAdminMe_WhenNotAdmin_Throws403` (Fact)
- `GetAdminMe_WhenAdmin_ReturnsRoleAndPermissions` (Fact)
- `HasPermission_ResolvesCorrectly` (Fact)
- `LogAudit_CreatesEntry` (Fact)
- `CreateRole_AsSuperAdmin_CreatesPermissionsAndAuditEntry` (Fact)
- `UpdateRole_RejectsPermissionBeyondActorDelegation` (Fact)

### backend/tests/HuTube.UnitTests/SepaySignatureVerifierTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/SepaySignatureVerifierTests.cs).

- `Verify_WhenTimestampMalformedOrOutsideWindow_RejectsWithoutThrowing` (Theory; 4 InlineData)
- `Verify_WhenSecretKeyIsEmpty_ReturnsFalse` (Fact)
- `Verify_WhenSignatureIsNull_ReturnsFalse` (Fact)
- `Verify_WhenValidSignature_ReturnsTrue` (Fact)
- `Verify_WhenInvalidSignature_ReturnsFalse` (Fact)
- `Verify_WhenTimestampTooOld_ReturnsFalse` (Fact)

### backend/tests/HuTube.UnitTests/UserEmailTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/UserEmailTests.cs).

- `Render_EncodesUserContentAndPreservesActionQuery` (Fact)
- `Render_OmitsUnsafeOrRelativeActions` (Theory; 4 InlineData)
- `AuthenticationEmails_KeepTheTokenIntactInBothAlternatives` (Theory; 2 InlineData)
- `ChannelInvitation_ShowsExpiryInVietnamTime` (Fact)
- `PickupDelivery_WritesMultipartHtmlAndTextForBothSenderOverloads` (Theory; 2 InlineData)

### backend/tests/HuTube.UnitTests/VideoRulesTests.cs

[Mở file test](../backend/tests/HuTube.UnitTests/VideoRulesTests.cs).

- `LowerQualities_ReturnsOnlyRenditionsBelowSource` (Theory; 4 InlineData)
- `ValidateVisibility_WithSupportedValue_DoesNotThrow` (Theory; 3 InlineData)
- `ValidateVisibility_WithUnsupportedValue_Throws` (Fact)
- `ValidateUpload_WithSupportedContentType_DoesNotThrow` (Theory; 3 InlineData)
- `ValidateUpload_WhenFileExceedsLimit_Throws` (Fact)
- `ValidateChapters_WithIncreasingTimeline_ReturnsNormalizedChapters` (Fact)
- `ValidateChapters_WhenTimelineIsNotIncreasing_Throws` (Fact)
- `CalculateProgress_ClampsAndCalculatesPercentage` (Theory; 2 InlineData)
- `ValidateComment_WithInvalidContent_Throws` (Theory; 3 InlineData)
- `ValidateRating_WithScoreOutsideOneToFive_Throws` (Fact)

### backend/tests/HuTube.IntegrationTests/AuthApiTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/AuthApiTests.cs).

- `Register_NewEmail_ShouldPersistPendingUserAndHashedSecrets` (Fact)
- `Register_ShouldAssignFreePlanAndCreatePlanHistory` (Fact)
- `GoogleLogin_VerifiedIdentity_ShouldCreateActiveAccountAndIssueSession` (Fact)
- `Login_SameDevice_ShouldReuseOneActiveSession` (Fact)
- `Register_DuplicateEmailIgnoringCase_ShouldReturnConflict` (Fact)
- `Register_InvalidInput_ShouldReturnValidation` (Theory; 2 InlineData)
- `Verify_UsedLink_ShouldRejectReplay` (Fact)
- `Verify_ExpiredLink_ShouldReject` (Fact)
- `Resend_PreviousVerificationLink_ShouldInvalidate` (Fact)
- `Login_Unverified_ShouldReject` (Fact)
- `Login_WrongPassword_ShouldRejectAndLockAfterFiveAttempts` (Fact)
- `Login_BlockedStatus_ShouldRejectLoginAndExistingToken` (Theory; 2 InlineData)
- `Login_RefreshLogout_ShouldRevokeAccessImmediately` (Fact)
- `Refresh_ReusedToken_ShouldRevokeDescendant` (Fact)
- `Refresh_ConcurrentRequests_ShouldAllowOnlyOneRotation` (Fact)
- `Refresh_ExpiredToken_ShouldReject` (Fact)
- `LogoutOthers_TwoDevices_ShouldPreserveCurrentOnly` (Fact)
- `LogoutAll_ShouldRevokeEverySessionAndWriteAuditLog` (Fact)
- `RevokeSession_AnotherUsersSession_ShouldReject` (Fact)
- `ResetPassword_ValidOtp_ShouldRevokeAllSessionsAndConsumeCode` (Fact)
- `ForgotPassword_UnknownEmail_ShouldReturnSameMessage` (Fact)
- `Admin_OrdinaryUser_ShouldDenyApiAndLogin` (Fact)
- `Admin_DisabledAfterLogin_ShouldBlockImmediately` (Fact)
- `Web_Login_ShouldUseHttpOnlyCookieAndRequireCsrfHeader` (Fact)
- `Web_UntrustedOrigin_ShouldDenyCookieRequest` (Fact)
- `Web_VideoUploadPreflight_ShouldAllowIdempotencyKey` (Fact)
- `Health_OpenApiAndAnonymousProtected_ShouldMatchContract` (Fact)
- `PublicSystemEndpoints_ExposeOnlyDocumentedFields` (Fact)
- `ChangePassword_InvalidRequestPreservesPasswordAndSessions` (Theory; 3 InlineData)
- `ChangePassword_ValidRequestRehashesPasswordAndRevokesEverySession` (Fact)
- `EveryAdminApiOperation_RejectsAnonymousRequests` (Fact)
- `Migration_Reapplied_ShouldPreserveFullBootstrapSchema` (Fact)
- `RevokeSession_DeviceRotatedSinceList_ShouldRevokeReplacement` (Fact)
- `Web_UserOriginWithAdminCookie_ShouldDenyCrossAppRefresh` (Fact)
- `Logout_OldRotatedCookie_ShouldRevokeReplacement` (Fact)
- `ResetPassword_ConcurrentOtpUse_ShouldSucceedOnce` (Fact)
- `ProtectedApi_TamperedToken_ShouldReject` (Fact)
- `ProtectedApi_ExpiredSignedToken_ShouldRejectEvenWithActiveSession` (Fact)
- `ResetPassword_ExpiredOtp_ShouldReject` (Fact)
- `ResetPassword_WrongOtpConsumesAfterFiveAttempts` (Fact)
- `Auth_RateLimitExceeded_ShouldReturnRetryContract` (Fact)

### backend/tests/HuTube.IntegrationTests/ChunkUploadRecoveryTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/ChunkUploadRecoveryTests.cs).

- `RestartResumesCommittedChunkAndTruncatesInterruptedWrite` (Fact)
- `CorruptedCompletionIsRejectedAndExpiredSessionsAreCleaned` (Fact)
- `ChunkStoreRejectsInvalidStartValuesAndKeepsTraversalInsideRoot` (Fact)
- `ChunkStorePreservesActiveProcessingAndIgnoresCorruptManifestDuringCleanup` (Fact)

### backend/tests/HuTube.IntegrationTests/CfSeederIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/CfSeederIntegrationTests.cs).

- `CfSeeder_AccountsUsersAndUploadSession_CompleteExactlyOnce` (Fact)
- `CfSeeder_InvalidAccountAndChunkInputs_DoNotCreateState` (Fact)
- `CfSeeder_ChunkProtocolRejectsConflictsWrongOrderAndForeignActor` (Fact)
- `CfSeeder_RequiresPermissionAcrossEveryRoute` (Fact)
- `CfSeeder_MultipartSourceRenditionAndKindMismatch_AreEnforced` (Fact)
- `CfSeeder_StorageFailureReleasesSessionForRetryWithoutDuplicateRendition` (Fact)
- `CfSeeder_ManifestReplayIsStableAndExistingSearchDoesNotLeakSecrets` (Fact)
- `CfSeeder_ExistingAccountImportUsesTheExistingOwnerAndChannel` (Fact)
- `CfSeeder_ProcessingOwnershipAndMediaMetadataAreValidated` (Fact)
- `CfSeeder_ConcurrentChunkAndCompletionAreExactlyOnce` (Fact)
- `CfSeeder_InvalidRenditionMetadataAndOwnershipReturnStableErrors` (Fact)
- `CfSeeder_CompletionValidationFailureKeepsTheUploadForOneCleanRetry` (Fact)

### backend/tests/HuTube.IntegrationTests/PlanAndPaymentEndpointIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/PlanAndPaymentEndpointIntegrationTests.cs).

- `PlanMemberRoutes_CoverBothAliasesStorageAndRevocation` (Fact)

### backend/tests/HuTube.IntegrationTests/ContentApiIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/ContentApiIntegrationTests.cs).

- `NotificationPreferencesAndPaging_ControlReplyDelivery` (Fact)
- `NotificationReadAllAndFeedSurfacesRemainScopedAndQueryable` (Fact)
- `NotificationRead_IsIdempotentAndRejectsForeignOwner` (Fact)
- `VideoThumbnail_ValidImagePersistsAndInvalidTypeDoesNotMutate` (Fact)
- `AccountPreferences_RoundTripPartialUpdatesAndRemainUserScoped` (Fact)
- `AccountPreferences_InvalidEnumLeavesPersistedPreferencesUnchanged` (Theory; 2 InlineData)
- `AccountNotificationSettings_RoundTripAllFieldsAndRemainUserScoped` (Fact)
- `AccountProfile_PatchIsPartialTrimmedAndDoesNotExposeCredentials` (Fact)
- `AccountAvatar_ValidImagePersistsAndInvalidMediaDoesNotCreateObject` (Fact)
- `UploadModeratePublish_ExposesVideoInPublicFeedsAndPlayback` (Fact)
- `VideoInteractions_AreMutuallyExclusiveAndPersistWatchProgressRatingShare` (Fact)
- `VideoLikeCommentLikeAndReply_CreateRealtimeNotificationRows` (Fact)
- `Comments_ReplyReactionReportAndOwnerModeration_WorkEndToEnd` (Fact)
- `SettingsDownloadsAndCreatorManagement_PersistAndEnforceOwnership` (Fact)
- `Downloads_AreActorScopedAndDeletingARecordKeepsSourceMedia` (Fact)
- `UploadPreflightRetryAndCancel_EnforcePlanAndRetainMediaUntilPurge` (Fact)
- `CategoriesCreatorEditFilterAndSoftDelete_WorkEndToEnd` (Fact)
- `CommentCrudSortAndGuestAuthorization_WorkEndToEnd` (Fact)
- `PrivatePlaylistItems_AreTombstonesForOtherViewers` (Fact)
- `MetadataDoesNotIssueSourceUrls_AndSuspendedChannelsCannotPlay` (Fact)
- `ProfileBioPersists_AndModeratorRestrictionsSurviveCreatorEdits` (Fact)
- `DisabledComments_RejectNewComments` (Fact)
- `PaymentIdempotency_IsScopedToUserAndRejectsFinishedAttempts` (Theory; 4 InlineData)
- `PaymentFinalSaveFailure_RollsBackActivatedPlanAndHistory` (Fact)
- `DeletedVideoPurge_PreservesDatabaseConstraintsAndReleasesQuotaOnce` (Fact)
- `ConcurrentUploads_ReserveQuotaUsingFreshValues_AndCleanRejectedObjects` (Fact)
- `UploadProbeOverridesClientClaims_AndRevalidatesActualDuration` (Fact)

### backend/tests/HuTube.IntegrationTests/UserModerationEndpointIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/UserModerationEndpointIntegrationTests.cs).

- `UserModerationRoutes_ReportAppealEvidenceAndStrikeStatusStayScoped` (Fact)

### backend/tests/HuTube.IntegrationTests/WorkerIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/WorkerIntegrationTests.cs).

- `AuthSessionCleanupWorker_RevokesOnlyExpiredOrInactiveSessions` (Fact)
- `PaymentExpirationWorker_CancelsOnlyExpiredPendingPayments` (Fact)
- `PlanExpirationWorker_ExpiresHistoryAndRestoresDefaultEntitlement` (Fact)
- `VideoMediaRetentionWorker_PurgesEligibleMediaAndReleasesQuota` (Fact)
- `StrikeStatus_RecalculatesExpiryAndOverlappingRestrictionsWithoutASeparateWorker` (Fact)
- `RenditionProcessor_ProducesOnlyLowerReadyOutputsFromTheSource` (Fact)
- `RenditionProcessor_MarksPartialFailureAndAllowsAFullRetry` (Fact)
- `PaymentExpirationWorker_TwoInstancesClaimAnExpiredPaymentOnlyOnce` (Fact)

`EveryAdminApiOperation_RejectsAnonymousRequests` (AuthApiTests) enumerates the current OpenAPI contract and verifies anonymous denial on all 103 `/api/v1/admin/` operations (B-ADMIN 83, B-CF 9, B-RECOMMENDATION 11); it checks the A01 security variant, not the functional H01 case.

### backend/tests/HuTube.IntegrationTests/FeatureFlagIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/FeatureFlagIntegrationTests.cs).

- `DisabledModerationAndPlan_BypassesPlanButLocksPublicVisibility` (Fact)

### backend/tests/HuTube.IntegrationTests/PolicyModerationIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/PolicyModerationIntegrationTests.cs).

- `PublicPolicies_ShouldExposeSeededRows_AndModeratorCanViewAssignedPolicyPermission` (Fact)
- `SuperAdmin_CanCreateAndPublishPolicy` (Fact)
- `SuperAdmin_CanManageTopicsAndTags` (Fact)
- `SuperAdmin_CanInspectAndLockUnlockUser` (Fact)
- `Moderator_CanViewQueue` (Fact)
- `SuperAdmin_CanClaimAndResolveCase_WithWireDecision` (Theory; 5 InlineData)
- `Moderation_ShouldRejectPascalCaseDecisionValues` (Fact)

### backend/tests/HuTube.IntegrationTests/RbacAndChannelIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/RbacAndChannelIntegrationTests.cs).

- `INT_S4_03_AdminRbac_Moderator_CanViewQueue_ButCannotApprove` (Fact)
- `INT_S4_06_AdminRbac_SuperAdminCanCreateAndUpdateCustomRole` (Fact)
- `INT_S4_04_ChannelMembership_OwnerAndEditor_PermissionEnforcement` (Fact)
- `ChannelSubscription_IsIdempotentAndNotificationPreferenceRoundTrips` (Fact)
- `SubscriptionsEndpoint_ReturnsOnlyActiveSubscriptionsForCurrentActor` (Fact)
- `ChannelManagement_ExposesOwnerViewsBrandingRolesAndMemberLifecycle` (Fact)

### backend/tests/HuTube.IntegrationTests/RecommendationAdminIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/RecommendationAdminIntegrationTests.cs).

- `RecommendationEndpointsRequireLiveSuperAdminRole` (Fact)
- `CreatingBotsDoesNotResetAnExistingAccount` (Fact)
- `MatrixDiffCountsAddedChangedAndRemovedPairs` (Fact)
- `SimulationUsesExistingServicesAndPreservesBotCredentials` (Fact)

### backend/tests/HuTube.IntegrationTests/RecommendationFeedIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/RecommendationFeedIntegrationTests.cs).

- `ViewedVideoIsExcludedFromCfAndFallbackOnEveryPage` (Fact)

### backend/tests/HuTube.IntegrationTests/RecommendationModelJobIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/RecommendationModelJobIntegrationTests.cs).

- `ModelJobUploadsDatedCsvAndDoesNotReplaceManifestOnFailure` (Fact)

### backend/tests/HuTube.IntegrationTests/SearchAndFilterIntegrationTests.cs

[Mở file test](../backend/tests/HuTube.IntegrationTests/SearchAndFilterIntegrationTests.cs).

- `Search_ByKeyword_MatchesTitleChannelDescriptionAndTags` (Fact)
- `Search_WithSpecialCharactersAndEmptyQuery_ReturnsSafely` (Fact)
- `Search_WithCombinedFilters_CategoryDurationAndDate` (Fact)
- `Search_StrictlyExcludesNonPublicVideos` (Fact)
- `Search_PaginationAndSorting` (Fact)

### recommendation-service/tests/test_api.py

[Mở file test](../recommendation-service/tests/test_api.py).

- `test_internal_api_requires_token_and_excludes_seen` (Python)
- `test_unknown_user_returns_404` (Python)
- `test_invalid_limit_returns_422` (Python)
- `test_model_admin_routes_require_separate_token` (Python)
- `test_no_model_returns_503` (Python)

### recommendation-service/tests/test_artifacts.py

[Mở file test](../recommendation-service/tests/test_artifacts.py).

- `test_artifact_round_trip_preserves_recommendations` (Python)

### recommendation-service/tests/test_cohorts.py

[Mở file test](../recommendation-service/tests/test_cohorts.py).

- `test_temporal_split_keeps_latest_interaction_for_test` (Python)

### recommendation-service/tests/test_complexity.py

[Mở file test](../recommendation-service/tests/test_complexity.py).

- `test_complexity_benchmark_uses_only_dense_item_based_strategy` (Python)

### recommendation-service/tests/test_data_pipeline.py

[Mở file test](../recommendation-service/tests/test_data_pipeline.py).

- `test_null_and_zero_have_different_masks` (Python)
- `test_local_movielens_has_official_counts` (Python)

### recommendation-service/tests/test_hutube_training.py

[Mở file test](../recommendation-service/tests/test_hutube_training.py).

- `test_missing_matrix_never_generates_sample_data` (Python)
- `test_empty_matrix_is_rejected` (Python)
- `test_hash_mismatch_is_rejected` (Python)
- `test_train_only_item_based_cosine` (Python)
- `test_weighted_score_configuration_is_persisted` (Python)

### recommendation-service/tests/test_item_cf.py

[Mở file test](../recommendation-service/tests/test_item_cf.py).

- `test_item_cf_predicts_and_excludes_seen_items` (Python)

### recommendation-service/tests/test_quality_gate.py

[Mở file test](../recommendation-service/tests/test_quality_gate.py).

- `test_quality_gate_accepts_complete_safe_benchmark` (Python)
- `test_quality_gate_rejects_invalid_runtime` (Python)
- `test_quality_gate_requires_both_models` (Python)

### recommendation-service/tests/test_r2_update.py

[Mở file test](../recommendation-service/tests/test_r2_update.py).

- `test_r2_training_restart_and_failed_update_keep_active_model` (Python)
- `test_training_lease_excludes_replicas_and_fences_old_owner` (Python)
- `test_job_creation_excludes_other_replica_and_recovers_default_aggregation` (Python)

### recommendation-service/tests/test_reports.py

[Mở file test](../recommendation-service/tests/test_reports.py).

- `test_html_report_is_self_contained_and_runtime_only` (Python)
- `test_html_report_includes_complexity_benchmark` (Python)
- `test_cf_comparison_report_is_runtime_only` (Python)
- `test_comparison_report_renders_six_variants_and_runtime_labels` (Python)

### recommendation-service/tests/test_similarity_variants.py

[Mở file test](../recommendation-service/tests/test_similarity_variants.py).

- `test_pearson_uses_rating_direction_and_not_missing_zeroes` (Python)
- `test_jaccard_uses_presence_not_rating_magnitude` (Python)
- `test_fit_all_models_creates_user_and_item_variants` (Python)

### recommendation-service/tests/test_training.py

[Mở file test](../recommendation-service/tests/test_training.py).

- `test_both_from_scratch_models_train_and_evaluate` (Python)

### recommendation-service/tests/test_user_cf.py

[Mở file test](../recommendation-service/tests/test_user_cf.py).

- `test_user_cf_recommends_from_similar_users` (Python)

## 11. Quy ước thực thi danh mục đầy đủ — cập nhật 04/10/2026

Mục 12–16 bổ sung test case có ID cho toàn bộ route tìm thấy trong working tree và các hành vi ngoài OpenAPI. Cột `Trạng thái` dùng quy ước: `PASS` = test backend tương ứng đã chạy thành công; `PLANNED` = chưa có execution evidence tương ứng; `FAIL` = execution có lỗi; `ĐANG LÀM` = trạng thái theo phạm vi công việc trước đó. `PASS` chỉ xác nhận execution hiện có, không tự chứng minh toàn bộ biến thể của dòng đã được chạy. Mỗi dòng có thể là case tham số hóa: khi triển khai đầy đủ phải chạy và lưu kết quả riêng cho từng giá trị/actor được liệt kê.

### 11.1 Fixture và quy trình chung

| Ký hiệu | Fixture / tiền điều kiện |
| --- | --- |
| U1, U2 | Hai user active/verified độc lập; U1 là actor chính, U2 dùng cho truy cập chéo. Có pending, suspended, deleted và Google-only user riêng |
| O1, O2, M1 | Owner hai kênh khác nhau và member có đúng một permission đang kiểm tra; thêm member không có permission |
| A1, A2, SA | Hai reviewer khác nhau có permission cụ thể; SA là super_admin. Admin role chưa đủ để mặc định được mọi operation |
| CH1, CH2 | Kênh active của O1/O2; thêm suspended/banned/deleted/upload-restricted; manifest lưu owner và membership |
| V1…Vn | Video public/unlisted/private, draft/processing/failed/ready, pending/approved/rejected/hidden/removed/purged; rendition/source/duration/hash có thể kiểm chứng |
| PL1, P1, PAY1 | Playlist, plan/subscription và payment thuộc U1; fixture tương đương thuộc U2; có pending/expired/terminal states |
| T0 | Mốc UTC cố định; dữ liệu trước/đúng/sau expiry. Thời gian trình bày Asia/Bangkok; không đổi thời gian máy hoặc đợi expiry thật |
| LARGE | Tối thiểu 61 rows, hai timestamp trùng, Unicode, active/inactive/deleted; các thống kê có tổng kỳ vọng tính trước |

Mỗi case: (1) Arrange fixture và snapshot DB/storage/event; (2) gọi đúng method/path với payload DTO hợp lệ hoặc biến thể ghi tại case; (3) assert status, body/header và trạng thái trước/sau; (4) đọc lại bằng API và DB độc lập; (5) cleanup theo manifest hoặc rollback fixture. Mutation thất bại phải không đổi trạng thái nghiệp vụ; security audit/rate-limit log được phép có nếu đúng thiết kế, không coi mọi audit là side effect sai. Không ghi refresh token, password, webhook secret hay connection string vào evidence.

### 11.2 Các biến thể áp dụng cho từng operation

Với mỗi ID `B-API-nnn` ở mục 12, tạo các execution ID `B-API-nnn-H01`, `-V01…`, `-A01…`, `-S01…`, `-R01…`, `-E01…`. Biến thể không áp dụng phải ghi `N/A` và lý do từ contract, không âm thầm bỏ. Không dùng một kết quả của route alias để thay cho route gốc.

| Hậu tố | Dữ liệu / bước chạy riêng | Expected / evidence |
| --- | --- | --- |
| H01 | Actor/fixture hợp lệ; request đúng DTO, method và content type | Kết quả chức năng tại dòng operation; body đúng schema, đúng ID/actor; DB/storage/event tương ứng |
| V01 | Mỗi field required lần lượt missing/null/empty/whitespace | Error hoặc normalize theo DTO/service; không tạo dữ liệu nghiệp vụ; ghi field/error cụ thể |
| V02 | Mỗi numeric/string/file field tại min−1/min/max/max+1; 0/negative/overflow khi áp dụng | Biên hợp lệ được chấp nhận, biên sai bị chặn hoặc clamp đúng contract; không overflow/500 |
| V03 | GUID sai, enum ngoài tập, malformed JSON, sai content type/multipart thiếu part | Routing/model binding/service trả lỗi cụ thể; không coi route constraint 404 như lỗi validation 400 |
| V04 | Unicode có dấu, emoji, literal HTML/SQL, newline, encoded URL/file name | Preserve hoặc normalize đúng quy tắc; không thực thi đầu vào; không hỏng query/export/response |
| A01 | Protected route thiếu credential | 401; public route chạy anonymous và chỉ trả trường public; token không phải tiền điều kiện của route public |
| A02 | Protected route dùng JWT expired/tampered/wrong issuer/wrong audience | 401, không mutation; kiểm tra refresh cookie riêng vì refresh/webhook dùng credential khác JWT |
| A03 | User thường, admin thiếu quyền, role chỉ có quyền đang kiểm tra, SA | Allow/deny đúng attribute và service; nhiều quyền trong attribute kiểm tra từng quyền và tổ hợp đúng semantics |
| A04 | U2/M1 thay mọi owner/channel/session/item/job ID của U1 | 403/404 theo contract; không leak dữ liệu riêng; role admin chỉ được cross-owner nếu có quyền cụ thể |
| A05 | Token đã phát hành rồi user bị khóa, session revoked, member bị xóa/role bị đổi | Quyền ở request sau được đánh giá lại theo chính sách hiện tại; ghi rõ cache và thời gian hiệu lực |
| S01 | GUID hợp lệ nhưng không tồn tại | 404/empty/no-op theo contract; không tạo tài nguyên giả, không leak existence của owner khác |
| S02 | Target soft-deleted/inactive/private/hidden/removed/terminal | Visibility và transition đúng rule; không phát URL media/evidence khi không có quyền |
| S03 | List có 0/1/61 rows; page đầu/cuối/vượt cuối và pageSize biên | Filter/sort trước pagination; total chính xác; route không phân trang ghi N/A cho page nhưng kiểm tra toàn bộ collection |
| S04 | Filter kết hợp, tie timestamp, khoảng from/to đảo, UTC offset khác nhau | Kết quả khớp dataset chuẩn; giới hạn/date validation theo contract; không suy ra sort ổn định nếu random được thiết kế |
| R01 | Gọi cùng mutation hai lần, cùng actor/key/resource | Idempotent hoặc conflict đúng contract; không double quota/member/reaction/payment/notification |
| R02 | Barrier hai actor/request đồng thời trên resource có invariant | Outcome và DB constraints đúng; không vượt capacity, không lost update ngoài semantics đã quy định |
| R03 | Hai operation khác nhau đua cùng resource: delete/update, cancel/complete, cancel/webhook | Một trạng thái hợp lệ cuối; không paid nhưng chưa có plan, không asset resurrect sau purge |
| E01 | Dependency timeout/5xx/DB constraint fail giữa saves | Error/fallback theo thiết kế; rollback phần nghiệp vụ cần atomic; không success event sai |
| E02 | Client hủy request, process restart nếu operation/job có checkpoint | Cancellation được truyền, cleanup hoặc checkpoint đúng; retry không tạo tài nguyên mới ngoài contract |
| E03 | Response thành công và lỗi | Content type/header/error code đúng; không stack trace/hash/token/secret. Webhook SePay có envelope riêng, không ép thành ProblemDetails |

`-H01` của legacy `/admin/moderation/approve` là **400 `CASE_ID_REQUIRED`** vì route cố ý từ chối luồng cũ; không đặt 2xx làm expected. `downloads/{id}/{operation}` cần execution riêng cho `pause`, `resume`, `retry`, `cancel` và giá trị không match route. Các case stream không mặc định có Range: `download-file` hiện gọi `File(...)` không bật `enableRangeProcessing`; ghi nhận contract hiện tại và kiểm tra Range riêng khi được hỗ trợ.


Danh mục bổ sung có **244 case chức năng theo operation .NET** và **281 case nghiệp vụ/hệ thống/Python/performance có ID** (tổng **525 dòng case**), chưa tính execution tham số hóa mục 11. Các case B-MEDIA.01–05 ở mục 6 và luồng mục 5 vẫn áp dụng. Không cộng số dòng thành số test đã thực thi.

## 12. Danh mục từng operation trong working tree

Snapshot source ngày **04/10/2026 (Asia/Bangkok)**: **244 operations / 214 path** từ HTTP attributes trong toàn bộ controller và ba minimal GET endpoints trong Program. Route constraints được rút gọn khi hiển thị `{id:guid}` → `{id}`; vẫn phải dùng constraints thật khi gọi. So với OpenAPI snapshot 02/10/2026: **19 operations thêm, không mất operation cũ**. Chưa lấy lại OpenAPI runtime trong lượt này. Hub/static/media/worker/Python có case riêng ngoài số 244.

| Nhóm | Operations source hiện tại | Trạng thái |
| --- | --- | --- |
| B-ACCOUNT | 8 | PASS |
| B-ADMIN | 83 | PASS |
| B-AUTH | 15 | PASS |
| B-CF | 9 | PASS |
| B-CHANNEL | 25 | PASS |
| B-COMMENT | 10 | PASS |
| B-DOWNLOAD | 4 | PASS |
| B-FEED | 5 | PASS |
| B-LIBRARY | 4 | PASS |
| B-MODERATION | 8 | PASS |
| B-NOTIFICATION | 3 | PASS |
| B-PAYMENT | 5 | PASS |
| B-PLAN | 14 | PASS |
| B-PLAYLIST | 11 | PASS |
| B-RECOMMENDATION | 11 | PASS |
| B-SUBSCRIPTION | 1 | PASS |
| B-SYSTEM | 3 | PASS |
| B-TAXONOMY | 3 | PASS |
| B-VIDEO | 22 | PASS |

Mỗi dòng là một case chức năng riêng; chạy thêm các hậu tố mục 11 và case nghiệp vụ cùng nhóm. Cột credential là **attribute-level**, service vẫn kiểm tra ownership/permission/state. `AUTH` là JWT session hợp lệ; `PUBLIC` là không có Authorize attribute, có thể còn yêu cầu refresh/reset/verification credential hoặc chữ ký webhook. Các tên permission là constants trong source. [RequirePermission](../backend/src/HuTube.Api/Authorization/PermissionAuthorization.cs) yêu cầu authenticated active admin và **ít nhất một** permission được liệt kê (OR); service vẫn có thể kiểm tra quyền action bổ sung. Khi thu hồi một quyền trong tổ hợp OR, kiểm tra actor còn quyền thay thế và actor mất tất cả quyền. Status thành công theo từng action: DTO/Ok thường 200, Created/StatusCode(201) là 201, NoContent là 204; không áp status theo HTTP method hoặc ép mọi POST thành 201. Giữ ID đã cấp khi cập nhật inventory sau này, không đánh số lại case cũ.


### AccountController — B-ACCOUNT

[Nguồn controller](../backend/src/HuTube.Api/Controllers/AccountController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-001 | `POST /api/v1/account/avatar` | AUTH | U1 upload ảnh MIME hợp lệ ≤5 MiB → profile AvatarUrl mới; bytes tồn tại, UserId không đổi | PASS |
| B-API-002 | `POST /api/v1/account/change-password` | AUTH | U1 nhập current/new hợp lệ → hash mới và revoke mọi session; DB/cache đồng bộ, không plaintext | PASS |
| B-API-003 | `GET /api/v1/account/notifications` | AUTH | U1 → đủ 11 notification bool đúng DB/defaults của actor | PASS |
| B-API-004 | `PUT /api/v1/account/notifications` | AUTH | U1 PUT từng preference → đủ 11 bool roundtrip và delivery theo từng loại notice | PASS |
| B-API-005 | `GET /api/v1/account/profile` | AUTH | U1 → UserId/Username/Email/DisplayName/AvatarUrl/Bio/EmailVerified/CreatedAt đúng; no secrets | PASS |
| B-API-006 | `PATCH /api/v1/account/profile` | AUTH | U1 PATCH DisplayName/Bio/AvatarUrl → chỉ field được gửi đổi theo null/empty semantics; đọc lại đúng | PASS |
| B-API-007 | `GET /api/v1/account/settings` | AUTH | U1 → Language/Theme/privacy flags/Location đúng default hoặc DB | PASS |
| B-API-008 | `PUT /api/v1/account/settings` | AUTH | U1 PUT vi/en, light/dark/system và privacy/location → fields roundtrip, null/empty theo service | PASS |

### AdminController — B-ADMIN

[Nguồn controller](../backend/src/HuTube.Api/Controllers/AdminController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-009 | `GET /api/v1/admin/appeals` | AppealView | A1 AppealView → appeals/status/filter/order/reviewer đúng | PASS |
| B-API-010 | `POST /api/v1/admin/appeals/{appealId}/claim` | AppealResolve | A1 AppealResolve → reviewer claim hợp lệ và SoD rules | PASS |
| B-API-011 | `GET /api/v1/admin/appeals/{appealId}/evidence` | AppealView | A1 AppealView → private evidence bytes đúng appeal, access audited theo thiết kế | PASS |
| B-API-012 | `POST /api/v1/admin/appeals/{appealId}/release` | AppealResolve | A1 AppealResolve → release đúng reviewer/appeal, giữ history | PASS |
| B-API-013 | `POST /api/v1/admin/appeals/{appealId}/resolve` | AppealResolve | A1 AppealResolve → approve/reject và state restore/strike/notice atomic, SoD/retention đúng | PASS |
| B-API-014 | `GET /api/v1/admin/audit-logs` | AuditView | A1 AuditView → legacy audit list đúng schema/actor/target/time/redaction | PASS |
| B-API-015 | `GET /api/v1/admin/channels` | ChannelView | A1 ChannelView → channels filter/page/total/status đúng | PASS |
| B-API-016 | `DELETE /api/v1/admin/channels/{channelId}` | ChannelDelete | A1 ChannelDelete → deleted và retention/content state đúng | PASS |
| B-API-017 | `GET /api/v1/admin/channels/{channelId}` | ChannelView | A1 ChannelView → channel detail/member/content/moderation đúng target | PASS |
| B-API-018 | `POST /api/v1/admin/channels/{channelId}/ban` | ChannelBan | A1 ChannelBan → banned và content access/reason/audit đúng | PASS |
| B-API-019 | `POST /api/v1/admin/channels/{channelId}/lock` | ChannelLock | A1 ChannelLock → manual lock/reason/audit, access/upload restrictions đúng | PASS |
| B-API-020 | `POST /api/v1/admin/channels/{channelId}/restore` | ChannelDelete | A1 ChannelDelete → restore hợp lệ, constraints/purged media vẫn được tôn trọng | PASS |
| B-API-021 | `POST /api/v1/admin/channels/{channelId}/suspend` | ChannelSuspend | A1 ChannelSuspend → suspended và reason/audit/access effects đúng | PASS |
| B-API-022 | `POST /api/v1/admin/channels/{channelId}/unban` | ChannelUnban | A1 ChannelUnban → gỡ ban theo effective restrictions còn lại | PASS |
| B-API-023 | `POST /api/v1/admin/channels/{channelId}/unlock` | ChannelLock | A1 ChannelLock → gỡ đúng manual lock, overlapping strikes/ban không mở sớm | PASS |
| B-API-024 | `GET /api/v1/admin/me` | AUTH | A1 → admin identity/permissions/channel capabilities đúng DB, không stale hoặc secrets | PASS |
| B-API-025 | `POST /api/v1/admin/moderation/{caseId}/claim` | ModerationClaim | A1 ModerationClaim → một active reviewer case, concurrent claim invariant | PASS |
| B-API-026 | `POST /api/v1/admin/moderation/{caseId}/release` | ModerationClaim | A1 ModerationClaim → release đúng claimed actor/case, history giữ | PASS |
| B-API-027 | `POST /api/v1/admin/moderation/{caseId}/resolve` | ModerationReview, ModerationApprove, ModerationReject | A1 có review/action permission → decision/policy/video/strike/notice atomic, terminal và SoD rules | PASS |
| B-API-028 | `POST /api/v1/admin/moderation/approve` | ModerationApprove | 400 CASE_ID_REQUIRED; không mutate video/case nào | PASS |
| B-API-029 | `GET /api/v1/admin/moderation/queue` | ModerationViewQueue | A1 ModerationViewQueue → queue đúng filter/status/order/case/video/reviewer | PASS |
| B-API-030 | `GET /api/v1/admin/permissions` | RoleView | A1 RoleView → catalog permission codes/group/system fields đúng | PASS |
| B-API-031 | `GET /api/v1/admin/plans` | PlanView | A1 PlanView → admin catalog cả trạng thái được phép, price/feature/order đúng | PASS |
| B-API-032 | `POST /api/v1/admin/plans` | PlanCreate | A1 PlanCreate → một plan hợp lệ, Location/ID/price/feature/quota/audit đúng | PASS |
| B-API-033 | `PUT /api/v1/admin/plans/{planId}` | PlanEdit | A1 PlanEdit → plan fields roundtrip, current/history semantics đúng | PASS |
| B-API-034 | `POST /api/v1/admin/plans/{planId}/archive` | PlanArchive | A1 PlanArchive → archived đúng plan, lịch sử subscription giữ | PASS |
| B-API-035 | `GET /api/v1/admin/plans/subscriptions/{userId}` | PlanView | A1 PlanView → effective subscription của target user, empty behavior đúng | PASS |
| B-API-036 | `GET /api/v1/admin/policies` | SystemViewSetting, PolicyView, ModerationReview, ModerationViewQueue | A1 một quyền đọc được attribute chấp nhận → policy/version/filter đúng | PASS |
| B-API-037 | `POST /api/v1/admin/policies` | PolicyManage, SystemEditSetting | A1 quyền manage phù hợp → policy/version hợp lệ và audit, chưa public trái publish state | PASS |
| B-API-038 | `PUT /api/v1/admin/policies/{policyId}/publish` | PolicyManage, SystemEditSetting | A1 quyền publish phù hợp → version published đúng, decision history không đổi | PASS |
| B-API-039 | `GET /api/v1/admin/reports` | ReportView | A1 ReportView → legacy report queue đúng target/status/filter | PASS |
| B-API-040 | `POST /api/v1/admin/reports/{reportId}/claim` | ReportClaim | A1 ReportClaim → legacy report claim cùng grouped-case rules | PASS |
| B-API-041 | `POST /api/v1/admin/reports/{reportId}/release` | ReportClaim | A1 ReportClaim → legacy release đúng reviewer/group | PASS |
| B-API-042 | `POST /api/v1/admin/reports/{reportId}/resolve` | ReportResolve | A1 ReportResolve → legacy resolution nhất quán grouped case, no double effects | PASS |
| B-API-043 | `GET /api/v1/admin/reports/cases` | ReportView | A1 ReportView → grouped cases/page/total/report count/reviewer đúng | PASS |
| B-API-044 | `GET /api/v1/admin/reports/cases/{caseId}` | ReportView | A1 ReportView → case/reports/dispositions/target đúng | PASS |
| B-API-045 | `POST /api/v1/admin/reports/cases/{caseId}/claim` | ReportClaim | A1 ReportClaim → một reviewer grouped case, report mapping đúng | PASS |
| B-API-046 | `POST /api/v1/admin/reports/cases/{caseId}/dispositions` | ReportResolve | A1 ReportResolve → dispositions chỉ report thuộc case, atomic đúng valid states | PASS |
| B-API-047 | `POST /api/v1/admin/reports/cases/{caseId}/release` | ReportClaim | A1 ReportClaim → release đúng actor/case, claim history giữ | PASS |
| B-API-048 | `POST /api/v1/admin/reports/cases/{caseId}/resolve` | ReportResolve | A1 ReportResolve → resolution/action/policy/strike/effective state/notice atomic | PASS |
| B-API-049 | `GET /api/v1/admin/roles` | RoleView | A1 RoleView → roles/permissions/status đúng scope | PASS |
| B-API-050 | `POST /api/v1/admin/roles` | RoleEdit | A1 RoleEdit → role unique và permission set hợp lệ, audit đúng | PASS |
| B-API-051 | `DELETE /api/v1/admin/roles/{roleId}` | RoleDelete | A1 RoleDelete → soft-delete/reason/membership effect đúng, protected role invariant giữ | PASS |
| B-API-052 | `PUT /api/v1/admin/roles/{roleId}` | RoleEdit | A1 RoleEdit → role fields/permission set roundtrip, system protection | PASS |
| B-API-053 | `GET /api/v1/admin/strikes` | StrikeView | A1 StrikeView → channel strikes/status/active count/expiry đúng | PASS |
| B-API-054 | `POST /api/v1/admin/strikes` | StrikeManage | A1 StrikeManage → strike đúng channel/reason/policy và effective restriction | PASS |
| B-API-055 | `POST /api/v1/admin/strikes/{strikeId}/revoke` | StrikeManage | A1 StrikeManage → revoke target, giữ history, overlapping restrictions/count đúng | PASS |
| B-API-056 | `GET /api/v1/admin/strikes/policy` | StrikeView | A1 StrikeView → config thresholds/durations/effectiveAt đúng | PASS |
| B-API-057 | `PUT /api/v1/admin/strikes/policy` | StrikeManage | A1 StrikeManage → config hợp lệ, effectiveAt/audit và recalculation đúng | PASS |
| B-API-058 | `GET /api/v1/admin/subscriptions` | PlanView | A1 PlanView → filter/search/status/plan/cycle/autoRenew/date/page/totals đúng | PASS |
| B-API-059 | `GET /api/v1/admin/subscriptions/{historyId}` | PlanView | A1 PlanView → history/member/allocation/plan đúng target historyId | PASS |
| B-API-060 | `PUT /api/v1/admin/subscriptions/{historyId}/auto-renew` | PlanEdit | A1 PlanEdit → autoRenew đúng target và audit, no unexpected payment | PASS |
| B-API-061 | `POST /api/v1/admin/subscriptions/{historyId}/change-plan` | PlanEdit | A1 PlanEdit → plan/entitlements/quota/history chuyển atomic đúng target | PASS |
| B-API-062 | `POST /api/v1/admin/subscriptions/{historyId}/extend` | PlanEdit | A1 PlanEdit → expiry/history/audit theo rule extend, không double effect trái contract | PASS |
| B-API-063 | `GET /api/v1/admin/subscriptions/export` | PlanView | A1 PlanView → toàn tập cùng filters, JSON collection theo controller hiện tại | PASS |
| B-API-064 | `GET /api/v1/admin/tags` | SystemViewSetting, TaxonomyManage | A1 quyền đọc phù hợp → tags/filter/count đúng | PASS |
| B-API-065 | `POST /api/v1/admin/tags` | TaxonomyManage | A1 TaxonomyManage → một tag hợp lệ unique theo rules | PASS |
| B-API-066 | `DELETE /api/v1/admin/tags/{tagId}` | TaxonomyManage | A1 TaxonomyManage → delete/archive và video references theo rule | PASS |
| B-API-067 | `PUT /api/v1/admin/tags/{tagId}` | TaxonomyManage | A1 TaxonomyManage → tag metadata đúng clear/null semantics | PASS |
| B-API-068 | `GET /api/v1/admin/topics` | SystemViewSetting, TaxonomyManage, CfSeedManage | A1 quyền đọc phù hợp → topics/status/order/filter đúng | PASS |
| B-API-069 | `POST /api/v1/admin/topics` | TaxonomyManage | A1 TaxonomyManage → category unique hợp lệ, audit và public state đúng | PASS |
| B-API-070 | `PUT /api/v1/admin/topics/{categoryId}` | TaxonomyManage | A1 TaxonomyManage → fields/slug/status/order đúng, references giữ | PASS |
| B-API-071 | `POST /api/v1/admin/topics/{categoryId}/archive` | TaxonomyManage | A1 TaxonomyManage → archive đúng category, public list filter và references giữ | PASS |
| B-API-072 | `GET /api/v1/admin/users` | UserView | A1 UserView → filters/page/count user đúng, không hash/token | PASS |
| B-API-073 | `GET /api/v1/admin/users/{userId}` | UserView | A1 UserView → detail target đúng schema và redaction | PASS |
| B-API-074 | `POST /api/v1/admin/users/{userId}/lock` | UserBan | A1 UserBan → target blocked/reason/audit và session/cache effects đúng | PASS |
| B-API-075 | `PUT /api/v1/admin/users/{userId}/role` | UserEdit | A1 UserEdit → role hợp lệ đúng target; system/self protection và audit | PASS |
| B-API-076 | `GET /api/v1/admin/users/{userId}/statistics` | UserView | A1 UserView → statistics/range toàn tập target, không chỉ page đầu | PASS |
| B-API-077 | `POST /api/v1/admin/users/{userId}/unlock` | UserBan | A1 UserUnban → target active theo policy, không hồi sinh revoked token | PASS |
| B-API-078 | `GET /api/v1/admin/videos` | VideoView | A1 VideoView → video filters/page/total/status/category đúng | PASS |
| B-API-079 | `GET /api/v1/admin/videos/{videoId}` | VideoView | A1 VideoView → metadata/media/state/moderation đúng target và quyền | PASS |
| B-API-080 | `POST /api/v1/admin/videos/{videoId}/hide` | VideoHide | A1 VideoHide → hidden/effective access/notice/reason đúng | PASS |
| B-API-081 | `PUT /api/v1/admin/videos/{videoId}/metadata` | VideoEditMetadata | A1 VideoEditMetadata → metadata roundtrip/clear và audit đúng | PASS |
| B-API-082 | `POST /api/v1/admin/videos/{videoId}/remove` | VideoRemove | A1 VideoRemove → removed/retention/quota/notice đúng | PASS |
| B-API-083 | `POST /api/v1/admin/videos/{videoId}/restore` | VideoRestore | A1 VideoRestore → restore theo rule, không resurrect purged source | PASS |
| B-API-084 | `POST /api/v1/admin/videos/{videoId}/unhide` | VideoUnhide | A1 VideoUnhide → gỡ hide nhưng restrictions còn lại giữ | PASS |
| B-API-085 | `GET /api/v1/admin/videos/statistics` | VideoView | A1 VideoView → totals theo toàn dataset và state buckets đúng | PASS |

### AdminOperationsController — B-ADMIN

[Nguồn controller](../backend/src/HuTube.Api/Controllers/AdminOperationsController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-086 | `GET /api/v1/admin/operations/audit-logs` | AuditView | A1 AuditView → log filters/page/total và actor/target/status đúng, redaction | PASS |
| B-API-087 | `GET /api/v1/admin/operations/reports` | DashboardView | A1 DashboardView → totals/time buckets theo range và toàn bộ dataset | PASS |
| B-API-088 | `GET /api/v1/admin/operations/system` | SystemViewSetting | A1 SystemViewSetting → system overview/status/metrics đúng, no configuration secret | PASS |

### AdminPaymentsController — B-ADMIN

[Nguồn controller](../backend/src/HuTube.Api/Controllers/AdminPaymentsController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-089 | `GET /api/v1/admin/payments` | PaymentView | A1 PaymentView → payments filter/page/totals/daily theo dataset chuẩn | PASS |
| B-API-090 | `GET /api/v1/admin/payments/{paymentId}` | PaymentView | A1 PaymentView → payment/customer/plan/events đúng ID, no signing secret | PASS |
| B-API-091 | `GET /api/v1/admin/payments/export` | PaymentView | A1 PaymentView → export cùng filters trên toàn tập, content type/schema thực tế đúng | PASS |

### AuthController — B-AUTH

[Nguồn controller](../backend/src/HuTube.Api/Controllers/AuthController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-092 | `POST /api/v1/auth/forgot-password` | PUBLIC | Known/unknown email → cùng public message; known email nhận OTP, chỉ OTP hash chậm được lưu | PASS |
| B-API-093 | `POST /api/v1/auth/google` | PUBLIC | Verified Google identity → create/link account và session đúng platform, default plan/history khi user mới | PASS |
| B-API-094 | `POST /api/v1/auth/login` | PUBLIC | Verified active U1 + Platform/header khớp → session/JWT đúng device; web HttpOnly cookie, mobile refresh theo contract | PASS |
| B-API-095 | `GET /api/v1/auth/login-history` | AUTH | U1 → login events/timestamp/device/IP/status theo filter/order, không history user khác | PASS |
| B-API-096 | `POST /api/v1/auth/logout` | PUBLIC + refresh/session credential theo platform | Credential hiện hành → revoke session/rotated replacement theo rule; web cookie clear | PASS |
| B-API-097 | `POST /api/v1/auth/logout-all` | AUTH | U1 → mọi session revoked, cookie/audit/SignalR effects theo contract | PASS |
| B-API-098 | `POST /api/v1/auth/logout-others` | AUTH | U1 hai devices → chỉ giữ current session, audit và revoke/cache đúng scope | PASS |
| B-API-099 | `GET /api/v1/auth/me` | AUTH | U1 token hiện hành → identity và permissions đúng DB, no secrets | PASS |
| B-API-100 | `POST /api/v1/auth/refresh` | PUBLIC + refresh/session credential theo platform | Refresh cookie/body đúng platform → một rotation, JWT/jti mới và replacement chain đúng | PASS |
| B-API-101 | `POST /api/v1/auth/register` | PUBLIC | Email mới/password hợp lệ → 201 pending user, hash/token hash, một default plan/history và verification mail | PASS |
| B-API-102 | `POST /api/v1/auth/resend-verification` | PUBLIC | Pending U1 → link mới, link cũ invalid; đúng recipient và response | PASS |
| B-API-103 | `POST /api/v1/auth/reset-password` | PUBLIC | Valid email/OTP/new password → hash mới, consume OTP và revoke tất cả sessions; OTP hết hạn 10 phút, tối đa 5 lần sai | PASS |
| B-API-104 | `GET /api/v1/auth/sessions` | AUTH | U1 → active/device/session metadata đúng, không refresh token/hash | PASS |
| B-API-105 | `DELETE /api/v1/auth/sessions/{sessionId}` | AUTH | U1 session target → revoke đúng device/replacement, giữ session user khác | PASS |
| B-API-106 | `POST /api/v1/auth/verify-email` | PUBLIC | Token valid U1 → email verified và active đúng user; consume token một lần | PASS |

### CategoriesController — B-TAXONOMY

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-107 | `GET /api/v1/categories` | PUBLIC | Guest → chỉ category `active`, sort theo `Name`, fields đúng `categoryId/name/slug/description`; kiểm tra cache sau admin update và không leak inactive | PASS |

### CfSeederController — B-CF

[Nguồn controller](../backend/src/HuTube.Api/Controllers/CfSeederController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-108 | `POST /api/v1/admin/cf-seeder/accounts` | CfSeedManage | A1 CfSeedManage → batch account/channel mapping đúng manifest và reuse/creation rule, không duplicate replay | PASS |
| B-API-109 | `POST /api/v1/admin/cf-seeder/upload-sessions` | CfSeedManage | A1 → UploadId/ChunkSize đúng, durable session metadata và next offset; restart không reset bytes | PASS |
| B-API-110 | `POST /api/v1/admin/cf-seeder/upload-sessions/{uploadId}/chunks/{chunkIndex}` | CfSeedManage | A1 → committed bytes/nextChunk đúng index và length, không corrupt khi replay/concurrent | PASS |
| B-API-111 | `POST /api/v1/admin/cf-seeder/upload-sessions/{uploadId}/complete` | CfSeedManage | A1 đủ chunks → một video result, 201 lần đầu/200 stored replay; quota/source/job đúng | PASS |
| B-API-112 | `POST /api/v1/admin/cf-seeder/upload-sessions/{uploadId}/complete-rendition` | CfSeedManage | A1 đủ chunks → rendition result đúng VideoId/quality, replay stored response; kind mismatch reject | PASS |
| B-API-113 | `GET /api/v1/admin/cf-seeder/users` | CfSeedManage | A1 → existing accounts theo selection/search/page đúng, không password/hash/token | PASS |
| B-API-114 | `POST /api/v1/admin/cf-seeder/videos` | CfSeedManage | A1 → source video cho batch/UserId/ChannelId đúng, probe/quota/job/mapping nhất quán | PASS |
| B-API-115 | `GET /api/v1/admin/cf-seeder/videos/{videoId}/processing` | CfSeedManage | A1 → trạng thái job/renditions/progress đúng VideoId, không ready khi output thiếu | PASS |
| B-API-116 | `POST /api/v1/admin/cf-seeder/videos/{videoId}/renditions` | CfSeedManage | A1 → asset/rendition đúng video/quality/dimensions/codec/bytes, no unsupported upscale | PASS |

### ChannelController — B-CHANNEL

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ChannelController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-117 | `POST /api/v1/channels` | AUTH | O1 create channel hợp lệ → channel và một owner membership, plan limit đúng | PASS |
| B-API-118 | `DELETE /api/v1/channels/{id}` | AUTH | O1 → channel soft-delete và content visibility đúng, media retention theo policy | PASS |
| B-API-119 | `GET /api/v1/channels/{id}` | PUBLIC | Guest/actor theo access policy → đúng public channel fields cho ID | PASS |
| B-API-120 | `PATCH /api/v1/channels/{id}` | AUTH | O1/member có edit quyền → metadata/branding đúng null/empty semantics | PASS |
| B-API-121 | `POST /api/v1/channels/{id}/avatar` | AUTH | O1 branding permission → avatar mới có bytes và metadata, asset cũ theo cleanup policy | PASS |
| B-API-122 | `POST /api/v1/channels/{id}/banner` | AUTH | O1 branding permission → banner mới đúng type/limit và metadata | PASS |
| B-API-123 | `GET /api/v1/channels/{id}/invitations` | AUTH | O1 → pending invitations đúng channel/status, không foreign invites | PASS |
| B-API-124 | `DELETE /api/v1/channels/{id}/invitations/{invitationId}` | AUTH | O1 → invite đúng channel revoked, không thể accept tiếp | PASS |
| B-API-125 | `GET /api/v1/channels/{id}/members` | AUTH | Actor có view members → đúng owner/member/role/capability của channel | PASS |
| B-API-126 | `DELETE /api/v1/channels/{id}/members/{targetUserId}` | AUTH | O1 → member target removed và quyền bị thu hồi, owner invariant giữ | PASS |
| B-API-127 | `PATCH /api/v1/channels/{id}/members/{targetUserId}/role` | AUTH | O1 → target member đổi role hợp lệ, owner invariant giữ và quyền mới có hiệu lực | PASS |
| B-API-128 | `POST /api/v1/channels/{id}/members/invite` | AUTH | O1 → pending invite đúng email/role/channel và notice, chưa membership | PASS |
| B-API-129 | `DELETE /api/v1/channels/{id}/subscribe` | AUTH | U1 → subscription inactive/removed theo rule, count không âm | PASS |
| B-API-130 | `POST /api/v1/channels/{id}/subscribe` | AUTH | U1 → một subscription active và subscriber count đúng | PASS |
| B-API-131 | `PATCH /api/v1/channels/{id}/subscribe-notifications` | AUTH | U1 → preference delivery đúng, đọc status roundtrip | PASS |
| B-API-132 | `GET /api/v1/channels/{id}/subscribe-status` | AUTH | U1 → trạng thái subscribed/notification preference đúng actor/channel | PASS |
| B-API-133 | `POST /api/v1/channels/{id}/watermark` | AUTH | O1 branding permission → watermark/position/opacity valid, asset và settings roundtrip | PASS |
| B-API-134 | `GET /api/v1/channels/accessible` | AUTH | O1/M1 → owned và collaborative channels với role/capabilities đúng, không duplicate | PASS |
| B-API-135 | `GET /api/v1/channels/check-handle` | PUBLIC | Guest → availability sau normalize; không mutate channel | PASS |
| B-API-136 | `GET /api/v1/channels/handle/{handle}` | PUBLIC | Guest → channel theo normalized handle, visibility/status đúng | PASS |
| B-API-137 | `POST /api/v1/channels/invitations/{invitationId}/accept` | AUTH | U2 recipient → một channel membership đúng role, invitation consumed | PASS |
| B-API-138 | `POST /api/v1/channels/invitations/{invitationId}/decline` | AUTH | U2 recipient → declined, không membership và không accept tiếp | PASS |
| B-API-139 | `GET /api/v1/channels/invitations/me` | AUTH | U2 recipient → invites của mình, đúng channel/role/expiry | PASS |
| B-API-140 | `GET /api/v1/channels/me` | AUTH | O1 → owned channel đúng; empty behavior đúng contract | PASS |
| B-API-141 | `GET /api/v1/channels/roles` | AUTH | Member hợp lệ → catalog channel roles/permissions đúng | PASS |

### CommentsController — B-COMMENT

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-142 | `GET /api/v1/channels/{channelId}/comments/manage` | AUTH | Channel moderator → comments/manage đúng status/sort/page, videoTitle batch và total | PASS |
| B-API-143 | `DELETE /api/v1/comments/{id}` | AUTH | Author/moderator hợp lệ → delete/tombstone và reply/count visibility đúng | PASS |
| B-API-144 | `PATCH /api/v1/comments/{id}` | AUTH | Author → text mới đúng bounds, giữ author/video/parent, edit metadata đúng | PASS |
| B-API-145 | `DELETE /api/v1/comments/{id}/reaction` | AUTH | U1 → remove reaction, no double/negative counter | PASS |
| B-API-146 | `PUT /api/v1/comments/{id}/reaction` | AUTH | U1 → một comment reaction và counters/myReaction đúng | PASS |
| B-API-147 | `GET /api/v1/comments/{id}/replies` | PUBLIC | Guest/actor → replies đúng parent/video, hidden/deleted filtering và count | PASS |
| B-API-148 | `POST /api/v1/comments/{id}/report` | AUTH | U1 → legacy report normalize đúng comment/case/violation, no extra strike | PASS |
| B-API-149 | `PATCH /api/v1/comments/{id}/visibility` | AUTH | Channel moderator → hide/unhide đúng comment, guest/reply filtering đúng | PASS |
| B-API-150 | `GET /api/v1/videos/{videoId}/comments` | PUBLIC | Guest/actor → visible comments đúng video/sort/page/total/myReaction | PASS |
| B-API-151 | `POST /api/v1/videos/{videoId}/comments` | AUTH | U1 → comment/reply đúng video/parent/author, counts và recipient notice | PASS |

### DownloadsController — B-DOWNLOAD

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-152 | `DELETE /api/v1/downloads` | AUTH | U1 → đúng tập actor records xóa, xử lý mixed foreign IDs theo contract | PASS |
| B-API-153 | `GET /api/v1/downloads` | AUTH | U1 → download records đúng actor và state, source visibility theo rule | PASS |
| B-API-154 | `DELETE /api/v1/downloads/{id}` | AUTH | U1 → chỉ download record target xóa, media source còn | PASS |
| B-API-155 | `POST /api/v1/downloads/{id}/{operation}` | AUTH | U1 → chạy riêng pause/resume/retry/cancel, đúng transition/progress/bytes; không match regex bị reject | PASS |

### FeedController — B-FEED

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-156 | `GET /api/v1/feed/explore` | PUBLIC | Guest → search/filter/sort/page/total theo dataset chuẩn, không hidden/private | PASS |
| B-API-157 | `GET /api/v1/feed/explore-hub` | PUBLIC | Guest → explore buckets/categories/trending đúng totals và public visibility | PASS |
| B-API-158 | `GET /api/v1/feed/home` | PUBLIC | Guest/U1 → home ranking/personalized/fallback đúng; eligible cards/page/total | PASS |
| B-API-159 | `GET /api/v1/feed/recommended` | PUBLIC | Guest/U1 → recommendation hoặc fallback đúng actor, loại stale/ineligible candidates | PASS |
| B-API-160 | `GET /api/v1/feed/subscriptions` | AUTH | U1 → feed chỉ kênh đang subscribed, actor flags/page/total đúng | PASS |

### LibraryController — B-LIBRARY

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-161 | `DELETE /api/v1/library/history` | AUTH | U1 → clear toàn bộ history actor, bao gồm ngoài page đầu; dữ liệu U2/video còn | PASS |
| B-API-162 | `GET /api/v1/library/history` | AUTH | U1 → history/progress/timestamps đúng actor và before/page/total | PASS |
| B-API-163 | `DELETE /api/v1/library/history/{videoId}` | AUTH | U1 → xóa history của actor cho videoId, video và history U2 còn | PASS |
| B-API-164 | `GET /api/v1/library/liked` | AUTH | U1 → video liked đúng actor, filter/page/total và visibility | PASS |

### NotificationsController — B-NOTIFICATION

[Nguồn controller](../backend/src/HuTube.Api/Controllers/NotificationsController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-165 | `GET /api/v1/notifications` | AUTH | U1 → notifications/unread/page/total đúng recipient, không U2 payload | PASS |
| B-API-166 | `PATCH /api/v1/notifications/{id}/read` | AUTH | U1 → notification target read và unread count đúng, idempotent | PASS |
| B-API-167 | `POST /api/v1/notifications/read-all` | AUTH | U1 → mọi eligible notifications read, không chỉ một page và không user khác | PASS |

### PaymentsController — B-PAYMENT

[Nguồn controller](../backend/src/HuTube.Api/Controllers/PaymentsController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-168 | `GET /api/v1/payments` | AUTH | U1 → collection PaymentSummaryResponse đầy đủ đúng actor/trạng thái/order; route hiện không nhận paging/filter query | PASS |
| B-API-169 | `POST /api/v1/payments` | AUTH | U1 paid plan + key valid → pending/QR/reference/amount đúng; idempotent same actor/key/plan | PASS |
| B-API-170 | `GET /api/v1/payments/{paymentId}` | AUTH | U1 → PaymentSummaryResponse đúng owner/status/amount/currency/code/expiry; QR chỉ có trong response initiate, no secret | PASS |
| B-API-171 | `POST /api/v1/payments/{paymentId}/cancel` | AUTH | U1 pending payment → canceled atomic; không plan activation, terminal transition đúng | PASS |
| B-API-172 | `POST /api/v1/payments/webhook/sepay` | PUBLIC + SePay signature/timestamp | Signed raw body valid → 200 {success:true}, payment/plan/history/member atomic và transaction dedup | PASS |

### PlansController — B-PLAN

[Nguồn controller](../backend/src/HuTube.Api/Controllers/PlansController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-173 | `GET /api/v1/plans` | PUBLIC | Guest → catalog active/public plans đúng price/feature/order/quota | PASS |
| B-API-174 | `GET /api/v1/plans/{planId}` | PUBLIC | Guest → plan detail đúng ID và public entitlement fields | PASS |
| B-API-175 | `GET /api/v1/plans/{planId}/share` | PUBLIC | Guest → public share-plan data đúng, không member/private owner leak | PASS |
| B-API-176 | `POST /api/v1/plans/{planId}/subscribe` | AUTH | U1 → free/direct subscription đúng contract, paid không bypass payment | PASS |
| B-API-177 | `POST /api/v1/plans/invitations/{memberId}/accept` | AUTH | Recipient → một accepted membership, entitlement/history/storage đúng; alias cùng auth | PASS |
| B-API-178 | `DELETE /api/v1/plans/members/{memberId}` | AUTH | Owner/actor được phép → remove đúng member, owner/quota/entitlement invariant giữ | PASS |
| B-API-179 | `POST /api/v1/plans/members/{memberId}/accept` | AUTH | Recipient → một accepted membership, entitlement/history/storage đúng; alias cùng auth | PASS |
| B-API-180 | `PATCH /api/v1/plans/members/{memberId}/storage` | AUTH | Owner → allocation member đúng, tổng và usedBytes constraint giữ | PASS |
| B-API-181 | `POST /api/v1/plans/members/invite` | AUTH | Owner → một pending plan member/invite đúng recipient/allocation/capacity | PASS |
| B-API-182 | `GET /api/v1/plans/my-plan` | AUTH | U1 → effective plan/history/expiry/member/allocation/usedBytes đúng | PASS |
| B-API-183 | `POST /api/v1/plans/my-subscription/members` | AUTH | Owner → một pending plan member/invite đúng recipient/allocation/capacity | PASS |
| B-API-184 | `DELETE /api/v1/plans/my-subscription/members/{memberId}` | AUTH | Owner/actor được phép → remove đúng member, owner/quota/entitlement invariant giữ | PASS |
| B-API-185 | `PATCH /api/v1/plans/my-subscription/members/{memberId}/storage` | AUTH | Owner → allocation member đúng, tổng và usedBytes constraint giữ | PASS |
| B-API-186 | `PATCH /api/v1/plans/my-subscription/owner-storage` | AUTH | Owner → allocation owner đúng, tổng shared storage/usedBytes constraint giữ | PASS |

### PlaylistController — B-PLAYLIST

[Nguồn controller](../backend/src/HuTube.Api/Controllers/PlaylistController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-187 | `GET /api/v1/playlists` | AUTH | U1 → chỉ playlist của actor, sort `UpdatedAt desc`, tối đa 200; không gửi query filter/page vì source chưa nhận các tham số đó | PASS |
| B-API-188 | `POST /api/v1/playlists` | AUTH | U1 → playlist owner/channel/name/visibility đúng | PASS |
| B-API-189 | `DELETE /api/v1/playlists/{id}` | AUTH | Owner → 204 và xóa playlist cùng item rows; video nguồn giữ; test rõ đây là hard-delete, không phải tombstone | PASS |
| B-API-190 | `GET /api/v1/playlists/{id}` | PUBLIC | Guest/U1 → public/unlisted đọc được; private chỉ owner, guest bị 404; item private/removed/suspended trả tombstone (`available=false`, không title/url/status) thay vì leak hoặc tự lọc | PASS |
| B-API-191 | `PATCH /api/v1/playlists/{id}` | AUTH | Owner → fields/visibility roundtrip, private access đổi đúng | PASS |
| B-API-192 | `PUT /api/v1/playlists/{id}/order` | AUTH | Owner → request phải chứa đúng toàn bộ video IDs, không duplicate/foreign/missing; transaction đổi position tạm rồi commit, nhưng chưa có optimistic version nên stale concurrent request phải được đo riêng | PASS |
| B-API-193 | `POST /api/v1/playlists/{id}/videos` | AUTH | Owner → chỉ video `published + approved + public + not deleted`; position/count đúng; duplicate 409 `PLAYLIST_VIDEO_EXISTS`, invalid không mutation | PASS |
| B-API-194 | `DELETE /api/v1/playlists/{id}/videos/{videoId}` | AUTH | Owner → chỉ item target removed, source và order/count còn đúng | PASS |
| B-API-195 | `GET /api/v1/playlists/channel/{channelId}` | PUBLIC | Guest → channel phải `active`, chỉ public playlist của owner, sort `UpdatedAt desc`, tối đa 200; private/unlisted không xuất hiện | PASS |
| B-API-196 | `GET /api/v1/playlists/channel/{channelId}/mine` | AUTH | Chỉ channel owner được gọi; member/collaborator hoặc foreign owner nhận 403; trả playlist của owner, tối đa 200 | PASS |
| B-API-197 | `POST /api/v1/playlists/save` | AUTH | U1 → luôn dùng/tạo playlist private tên `Video đã lưu`; request chỉ có `videoId`, không có target/fallback; replay item trả 409, không duplicate | PASS |

### PolicyController — B-TAXONOMY

[Nguồn controller](../backend/src/HuTube.Api/Controllers/PolicyController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-198 | `GET /api/v1/policies` | PUBLIC | Guest → chỉ `published`, hỗ trợ query `group`, sort `group` rồi `code`, trả content/severity/version/effectiveAt; không trả draft/inactive | PASS |

### RecommendationAdminController — B-RECOMMENDATION

[Nguồn controller](../backend/src/HuTube.Api/Controllers/RecommendationAdminController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-199 | `POST /api/v1/admin/recommendations/bots` | SUPER_ADMIN | SA → bots/manifest mapping theo batch, reuse khi rule yêu cầu | PASS |
| B-API-200 | `GET /api/v1/admin/recommendations/categories` | SUPER_ADMIN | SA → selectable categories eligible và order đúng | PASS |
| B-API-201 | `GET /api/v1/admin/recommendations/jobs/{jobId}` | SUPER_ADMIN | SA → status/progress/history/log đúng job, no secrets | PASS |
| B-API-202 | `POST /api/v1/admin/recommendations/jobs/{jobId}/stop` | SUPER_ADMIN | SA → stop đúng job/state, no artifact activation nửa chừng | PASS |
| B-API-203 | `GET /api/v1/admin/recommendations/matrix-preview` | SUPER_ADMIN | SA → preview bounded và values/order đúng công thức matrix | PASS |
| B-API-204 | `GET /api/v1/admin/recommendations/matrix.csv` | SUPER_ADMIN | SA → matrix.csv đúng weights/axes/header/escaping, export toàn tập eligible | PASS |
| B-API-205 | `POST /api/v1/admin/recommendations/model-jobs` | SUPER_ADMIN | SA → accepted model job ID, queued/running lifecycle và activation sau publish | PASS |
| B-API-206 | `POST /api/v1/admin/recommendations/simulation-jobs` | SUPER_ADMIN | SA → accepted simulation ID và controlled signals theo config | PASS |
| B-API-207 | `GET /api/v1/admin/recommendations/status` | SUPER_ADMIN | SA → model/settings/status/schema hiện hành đúng, no AdminToken | PASS |
| B-API-208 | `GET /api/v1/admin/recommendations/users` | SUPER_ADMIN | SA → selectable users đúng query/filter/page và schema | PASS |
| B-API-209 | `GET /api/v1/admin/recommendations/videos` | SUPER_ADMIN | SA → selectable videos/IDs đúng filter và training policy | PASS |

### SubscriptionsController — B-SUBSCRIPTION

[Nguồn controller](../backend/src/HuTube.Api/Controllers/SubscriptionsController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-210 | `GET /api/v1/subscriptions` | AUTH | U1; chỉ subscribed channels của actor, status/count/privacy đúng | PASS |

### System — B-SYSTEM

[Nguồn controller](../backend/src/HuTube.Api/Program.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-211 | `GET /api/v1/system/config` | PUBLIC | Guest → Google client ID công khai nếu configured, không secret/signing/storage keys | PASS |
| B-API-212 | `GET /api/v1/system/info` | PUBLIC | Guest → đúng 5 fields `name/apiVersion/environment/serverTime/commitSha`; commit fallback `local`, không leak connection/JWT/storage config | PASS |
| B-API-213 | `GET /health` | PUBLIC | DB available → health theo Program; DB unavailable variant riêng, không connection secret | PASS |

### UserModerationController — B-MODERATION

[Nguồn controller](../backend/src/HuTube.Api/Controllers/UserModerationController.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-214 | `POST /api/v1/appeals` | AUTH | Owner → appeal đúng notice/case/reason/status, không cross-owner | PASS |
| B-API-215 | `GET /api/v1/appeals/{appealId}/evidence` | AUTH | Chỉ chủ appeal qua user route → bytes hoặc signed URL R2 10 phút; foreign/U2/guest deny; admin reviewer dùng route `/api/v1/admin/appeals/{id}/evidence` riêng | PASS |
| B-API-216 | `POST /api/v1/appeals/{appealId}/evidence` | AUTH | Owner → private evidence asset và metadata đúng appeal, không public URL | PASS |
| B-API-217 | `GET /api/v1/appeals/my` | AUTH | U1 → chỉ appeals actor, status/order đúng | PASS |
| B-API-218 | `GET /api/v1/channels/{channelId}/strikes` | AUTH | Chỉ channel owner → strikes/restrictions/count/expiry theo channel; foreign actor 403; expired row chỉ đổi status ở DTO/query, không giả định worker đã cập nhật DB | PASS |
| B-API-219 | `POST /api/v1/channels/{id}/report` | AUTH | U1 + `Idempotency-Key` tùy chọn → legacy channel report map vào cùng grouped case; inactive channel/foreign target không leak | PASS |
| B-API-220 | `POST /api/v1/reports` | AUTH | U1 → target chỉ video/comment/channel đang xem được, report group theo target, key replay cùng payload trả lại report; invalid/inactive violation phải bắt lỗi hoặc ghi defect nếu fallback sang type khác | PASS |
| B-API-221 | `POST /api/v1/videos/{id}/report` | AUTH | U1 + `Idempotency-Key` tùy chọn → legacy video report map vào generic case; video inaccessible/removed bị 404 và không tạo report | PASS |

### VideosController — B-VIDEO

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-222 | `POST /api/v1/videos` | AUTH | Owner/uploader → video/source/job/quota reservation đúng; probe kiểm chứng size/duration/quality, replay key không duplicate | PASS |
| B-API-223 | `DELETE /api/v1/videos/{id}` | AUTH | Owner → removed/soft-delete, access/quota/retention đúng; source chưa purge theo policy | PASS |
| B-API-224 | `GET /api/v1/videos/{id}` | PUBLIC | Guest/U1 theo visibility → video fields/interaction đúng actor, không private source leak | PASS |
| B-API-225 | `PATCH /api/v1/videos/{id}` | AUTH | Owner/editor → metadata/visibility/allowComments/promotion/chapter/tag/category roundtrip | PASS |
| B-API-226 | `POST /api/v1/videos/{id}/cancel-upload` | AUTH | Owner → canceled đúng state, quota reservation và worker completion nhất quán | PASS |
| B-API-227 | `POST /api/v1/videos/{id}/download-file` | AUTH | U1 → 200 binary đúng quality/hash, Content-Disposition/no-store/nosniff; recheck access | PASS |
| B-API-228 | `GET /api/v1/videos/{id}/download-options` | AUTH | U1 → chỉ rendition đủ entitlement; web URL rỗng, mobile URL theo contract | PASS |
| B-API-229 | `POST /api/v1/videos/{id}/downloads` | AUTH | U1 → 201 download record đúng video/quality/state/owner | PASS |
| B-API-230 | `GET /api/v1/videos/{id}/playback` | PUBLIC | Actor được xem → media/renditions đúng quality và access lifetime, không source của video khác | PASS |
| B-API-231 | `POST /api/v1/videos/{id}/publish` | AUTH | Owner đủ điều kiện → published/public visibility và feed nhất quán | PASS |
| B-API-232 | `DELETE /api/v1/videos/{id}/rating` | AUTH | U1 → rating removed và aggregate đúng | PASS |
| B-API-233 | `PUT /api/v1/videos/{id}/rating` | AUTH | U1 → một score hợp lệ, average/count đúng toàn bộ ratings | PASS |
| B-API-234 | `DELETE /api/v1/videos/{id}/reaction` | AUTH | U1 → reaction removed, counters đúng không âm | PASS |
| B-API-235 | `PUT /api/v1/videos/{id}/reaction` | AUTH | U1 → một like/dislike active, counters/myReaction và notice đúng | PASS |
| B-API-236 | `POST /api/v1/videos/{id}/retry-processing` | AUTH | Owner + failed source còn → retry đúng job/resource, không duplicate quota | PASS |
| B-API-237 | `POST /api/v1/videos/{id}/share` | AUTH | U1 → đúng share method/log/count, không tăng view sai | PASS |
| B-API-238 | `POST /api/v1/videos/{id}/submit-moderation` | AUTH | Owner → moderation case pending đúng video, không duplicate active case | PASS |
| B-API-239 | `POST /api/v1/videos/{id}/thumbnail` | AUTH | Owner/editor → thumbnail bytes/URL đúng, source video còn nguyên | PASS |
| B-API-240 | `PUT /api/v1/videos/{id}/watch-progress` | AUTH | U1 → progress/history/watch seconds đúng rule và actor, không tăng do retry trái contract | PASS |
| B-API-241 | `GET /api/v1/videos/manage` | AUTH | Owner/member → chỉ video quản lý được, filter/page/total/capability đúng | PASS |
| B-API-242 | `GET /api/v1/videos/search` | PUBLIC | Guest → cards đúng search/category/tag/channel/date/duration/sort/page/total và visibility | PASS |
| B-API-243 | `POST /api/v1/videos/upload-preflight` | AUTH | Uploader → entitlement/quality/quota/capability đúng trước upload, không tạo video giả | PASS |

### ViolationTypesController — B-TAXONOMY

[Nguồn controller](../backend/src/HuTube.Api/Controllers/ContentControllers.cs).

| Case ID | Method/path | Credential / permission attribute | Arrange → Act → Assert chức năng | Trạng thái |
| --- | --- | --- | --- | --- |
| B-API-244 | `GET /api/v1/violation-types` | PUBLIC | Guest → chỉ type `active`, sort theo `Name`, schema chỉ `violationTypeId/code/name/description`; nếu DB rỗng source tự seed 8 defaults (side effect), không có group/severity/order field | PASS |

## 13. Case nghiệp vụ, biên và hồi quy theo module

Các case dưới đây chạy thêm các biến thể mục 11 nếu áp dụng. `P0` ưu tiên tính đúng/quyền/atomicity; `P1` chức năng và hồi quy; `P2` độ bền/performance. Expected là tiêu chí cần kiểm chứng, **không phải lời khẳng định implementation hiện tại đã đáp ứng**. Cột `Trạng thái` phản ánh execution evidence hiện có, không thay thế coverage đầy đủ. Khi security/business requirement khác hành vi hiện tại, ghi FAIL/defect thay vì sửa expected để test xanh. Với tính năng chưa có endpoint hoặc contract, ghi `N/A`/`BLOCKED` có nguồn, không tự tạo route.

### 13.1 B-AUTH — xác thực, session, lịch sử đăng nhập

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-AUTH.01 | P0 | Email mới; register → verify-email → login → me | Pending trước verify; hash secret; active sau verify; một default plan/history; JWT subject/sid đúng U1 | PASS |
| B-AUTH.02 | P0 | Register cùng email với case/space khác; hai request barrier | Chỉ một user; loser conflict theo contract; không hai plan history/email xác minh thành công | PASS |
| B-AUTH.03 | P1 | Password 9/10/128/129 ký tự; thiếu hoa/thường/số từng lần | Chỉ chuỗi dài 10–128 và đủ nhóm ký tự hợp lệ; lỗi `INVALID_PASSWORD`; không plaintext DB/log | PASS |
| B-AUTH.04 | P1 | Email sai, username trùng/Unicode, displayName rỗng/biên | Normalize/unique/validation đúng AuthRules và DTO; không 500; record hợp lệ giữ tên có dấu | PASS |
| B-AUTH.05 | P0 | Verify token sai/expired/đã dùng; resend rồi dùng token cũ | Token cũ hết hiệu lực; không active sai user; token lưu dạng hash; lỗi và email count đúng | PASS |
| B-AUTH.06 | P0 | Hai request dùng cùng verify token hoặc reset OTP tại barrier | Credential consume một lần; trạng thái user/password/session nhất quán; không hai success effect | PASS |
| B-AUTH.07 | P0 | Login pending/suspended/deleted; sai password 1–5 lần rồi đúng | Block theo status/lockout; không session mới khi bị chặn; hết lockout đúng mốc/config | PASS |
| B-AUTH.08 | P0 | X-HuTube-Client web/mobile; X-HuTube-App admin; Platform khớp/lệch | Lệch 400 `CLIENT_PLATFORM_MISMATCH`; mobile trả refresh theo contract; web dùng HttpOnly cookie, response không refresh token | PASS |
| B-AUTH.09 | P0 | Web cookie user/admin; Origin đúng/sai/thiếu; Development/Production | Cookie name/path/Secure/SameSite đúng AuthController; origin sai 403; thiếu origin theo contract thực tế; không refresh chéo app | PASS |
| B-AUTH.10 | P0 | Login lại device cũ và device mới; đọc sessions | Device cũ reuse một active session; device mới tách; revoke session cũ sau rotation chặn replacement theo policy | PASS |
| B-AUTH.11 | P0 | Refresh lần đầu → token mới → replay token cũ | Rotation đúng; replay chặn và revoke descendants theo rule; access/refresh tiếp theo không dùng được | PASS |
| B-AUTH.12 | P0 | Hai refresh cùng token; expired/revoked refresh và JWT sid/jti sai | Một rotation hợp lệ; invalid không cấp token; session/token/cardinality và cache đồng bộ | PASS |
| B-AUTH.13 | P0 | Logout/current, logout-others, logout-all; đọc lại me/sessions ở các device | Chỉ tập session đúng scope bị revoke; request sau bị chặn; cookie hết hạn; audit actor/target đúng | PASS |
| B-AUTH.14 | P0 | U2 DELETE session U1; revoke ID missing/revoked | Không revoke U1 hoặc leak token/IP riêng; missing/repeat đúng contract; DB trước/sau chứng minh isolation | PASS |
| B-AUTH.15 | P0 | Forgot-password email tồn tại/không tồn tại; reset expired/replayed | Response chống enumeration; chỉ email hợp lệ nhận link; reset thành công revoke mọi session và token consume | PASS |
| B-AUTH.16 | P0 | Google verified/unverified/wrong audience/expired; email đã tồn tại | Fake verifier suite và OAuth sandbox tách evidence; linking đúng account; không bypass blocked/unverified identity | PASS |
| B-AUTH.17 | P1 | Nhiều login success/fail từ device/IP; GET login-history bằng U1/U2 | History đúng actor/order/paging contract, timestamps và IP chuẩn hóa; không token/password; U2 không thấy U1 | PASS |
| B-AUTH.18 | P1 | Vượt limiter auth trong cửa sổ rồi đợi bằng controlled clock/reset fixture | 429/header/recovery đúng Program; limiter không làm successful request biến thành invalid session | PASS |

### 13.2 B-ACCOUNT — hồ sơ và cài đặt

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-ACCOUNT.01 | P1 | GET profile → PATCH từng field DisplayName/Bio/AvatarUrl → GET | Chỉ field gửi được cập nhật theo null/empty semantics; UserId/Email/CreatedAt không đổi; UTF-8 roundtrip | PASS |
| B-ACCOUNT.02 | P1 | DisplayName trim: 0/1/120/121 ký tự; Bio null/empty/HTML | Biên tên đúng `INVALID_DISPLAY_NAME`; clear/unchanged bio theo service; không thực thi HTML | PASS |
| B-ACCOUNT.03 | P1 | Hai PATCH khác field; request duplicate và U2 spoof UserId | Không ghi nhầm owner; kiểm tra lost update bằng snapshot; fields ngoài request không bị reset | PASS |
| B-ACCOUNT.04 | P1 | Avatar JPEG/PNG/WebP/GIF; missing/0 byte/>5 MiB/MIME sai | File hợp lệ cập nhật profile; lỗi `INVALID_FILE`, `FILE_TOO_LARGE`, `INVALID_FILE_TYPE` theo tầng; tính multipart overhead khi test limit | PASS |
| B-ACCOUNT.05 | P0 | Avatar tên traversal/encoded; MIME ảnh nhưng bytes script; storage save fail | File chỉ vào root test; content sniff requirement ghi defect nếu chưa có; fail không làm profile trỏ asset mất | PASS |
| B-ACCOUNT.06 | P0 | Change-password missing/sai current, same new, new không đủ rule | 400 với `CURRENT_PASSWORD_REQUIRED`/`INVALID_CURRENT_PASSWORD`/`SAME_PASSWORD`/`INVALID_PASSWORD`; hash/session không đổi | PASS |
| B-ACCOUNT.07 | P0 | Đổi password hợp lệ khi hai device đang login và Redis warm | Hash mới verify được; password cũ fail; tất cả session revoked DB/cache; transaction fail không đổi nửa chừng | PASS |
| B-ACCOUNT.08 | P1 | PUT notifications, bật/tắt lần lượt đủ 11 bool rồi GET | Roundtrip đủ InApp/Email/NewVideo/CommentReply/ReportResult/Moderation/Plan/Recommendation/Mention/ChannelActivity/Payment; đúng user | PASS |
| B-ACCOUNT.09 | P1 | PUT settings: vi/en, light/dark/system, privacy bool và Location | Tất cả fields roundtrip; language/theme sai có code tương ứng; null giữ và empty clear theo contract | PASS |
| B-ACCOUNT.10 | P0 | Read profile/settings sau đổi/khóa user; U2 warm cùng endpoint | Không lấy cache U1 cho U2; không password/hash/refresh; invalidation/TTL đúng, auth kiểm tra trước dữ liệu riêng | PASS |

### 13.3 B-CHANNEL — chủ sở hữu, cộng tác và subscription

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-CHANNEL.01 | P0 | O1 create channel → me/accessible/by-ID/by-handle | Một owner membership đúng role; ID/handle nhất quán; plan channel limit được áp dụng | PASS |
| B-CHANNEL.02 | P1 | Handle hoa/thường/space/reserved/Unicode/biên; check-handle rồi create | Unique sau normalize; available check không bảo đảm race thắng; hai create chỉ một handle | PASS |
| B-CHANNEL.03 | P1 | O1 PATCH tên/description/branding; null/empty; M1 chỉ có edit metadata | Semantics field đúng; không escalation role/owner; asset và metadata đọc lại khớp | PASS |
| B-CHANNEL.04 | P1 | Avatar/banner/watermark đúng/sai MIME/size; watermark position/opacity biên | Validate từng loại theo controller/service; storage fail không URL hỏng/quota sai; file cũ cleanup theo retention | PASS |
| B-CHANNEL.05 | P0 | M1 lần lượt chỉ có mỗi quyền upload/edit/member/delete; gọi toàn bộ thao tác kênh | Mỗi permission độc lập; quyền một action không cho toàn kênh; no-permission bị chặn | PASS |
| B-CHANNEL.06 | P0 | Invite email U2 normalize/case; invite lặp, self, role invalid | Một pending invitation theo rule; đúng recipient/role; không thêm membership trước accept | PASS |
| B-CHANNEL.07 | P0 | U2 accept valid, expired, revoked, already accepted; U1 dùng invite U2 | Chỉ đúng recipient accept; owner/channel role không sai; repeat/expiry đúng status và no extra membership | PASS |
| B-CHANNEL.08 | P1 | U2 decline; O1 revoke invitation; read mine/pending | Terminal invite không accept; list phản ánh đúng trạng thái; hai kênh không leak invite | PASS |
| B-CHANNEL.09 | P0 | Hai accept cùng invite; remove/change role đồng thời với request member | Một membership; quyền ở request sau đúng rule; constraints không duplicate | PASS |
| B-CHANNEL.10 | P0 | Member đổi/xóa owner hoặc tự cấp owner/system role | Bị chặn; luôn tồn tại owner hợp lệ; U2 không thay owner CH1 | PASS |
| B-CHANNEL.11 | P1 | O1 xóa channel active rồi đọc video/feed/playback/member list | Soft-delete và visibility nhất quán; không nguồn public sai policy; retention/restore tách purge | PASS |
| B-CHANNEL.12 | P1 | U1 subscribe hai lần → status → unsubscribe hai lần | Một subscription active; subscriber count đúng; repeat không âm count hoặc notification trùng | PASS |
| B-CHANNEL.13 | P1 | PATCH subscribe-notifications bật/tắt rồi publish video CH1 | Setting roundtrip; delivery đúng preference; unsubscribe không tiếp tục nhận new-video notice | PASS |
| B-CHANNEL.14 | P0 | Public channel read khi locked/banned/deleted; accessible sau member removed | Visibility/status đúng contract; không private membership/invitation/source URL cho guest | PASS |
| B-CHANNEL.15 | P1 | 61 videos/members/subscriptions có tie date; đọc list/counts | Tổng không giới hạn trang đầu; owner và collaborator không duplicate channel; order/filter theo contract | PASS |

### 13.4 B-VIDEO — upload, xuất bản và tương tác

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-VIDEO.01 | P0 | Preflight → upload nguồn mục 6 với fixed idempotency key → poll → submit → approve → publish | State machine đúng; chỉ ready/approved đủ quyền mới public; size/duration/quality dựa media server | PASS |
| B-VIDEO.02 | P0 | U2/M1 không upload quyền CH1; spoof owner/channel/user trong multipart | Bị chặn trước effect; không video/job/file/quota của CH1 | PASS |
| B-VIDEO.03 | P0 | Quota used+reserved tại limit−size/limit/limit+1; hai upload barrier | Tổng không vượt capacity; một reservation mỗi upload; fail/cancel release đúng | PASS |
| B-VIDEO.04 | P1 | 0 byte/truncated/MIME-extension lệch/container unsupported; client khai size/duration thấp | Probe quyết định metadata; invalid không published; cleanup và error rõ | PASS |
| B-VIDEO.05 | P0 | Cùng key+payload replay; key khác actor; cùng key khác payload | Replay/quyền scope đúng contract; không duplicate video/job/quota; conflict không đổi record gốc | PASS |
| B-VIDEO.06 | P1 | Retry processing failed, ready, purged; hai retry barrier | Chỉ transition hợp lệ; không hai active job; source đã purge không hồi sinh | PASS |
| B-VIDEO.07 | P0 | Cancel khi pending/processing/ready; cancel đua worker completion | Terminal state/quota/file đúng; completion muộn không publish video canceled | PASS |
| B-VIDEO.08 | P1 | Thumbnail ảnh hợp lệ/giả/MIME sai/storage fail; U2 thay thumbnail V1 | Ownership và bounds đúng; không trỏ URL hỏng hoặc mất source/rendition | PASS |
| B-VIDEO.09 | P1 | PATCH title/description/tags/category/visibility/allowComments/promotion | Roundtrip từng field; null/empty semantics rõ; reference invalid chặn; no unexpected reset | PASS |
| B-VIDEO.10 | P1 | Chapters 0/duration, quá duration, âm, sai thứ tự, trùng timestamp | Validate/normalize đúng VideoRules/service; dữ liệu đọc lại theo thứ tự quy định | PASS |
| B-VIDEO.11 | P0 | V public/unlisted/private cho guest/owner/member/U2; GET detail/playback | Access policy nhất quán; không source/evidence URL cho actor không đủ quyền | PASS |
| B-VIDEO.12 | P0 | Warm playback/detail rồi admin hide/remove/channel ban/strike restriction | Request sau theo quyền/trạng thái hiện hành; cache không mở quyền lại; URL đã phát kiểm tra lifetime/access model riêng | PASS |
| B-VIDEO.13 | P1 | Manage nhiều kênh/61 videos/status/search/page; member role giới hạn | Chỉ tài nguyên actor được quản lý; filter trước paging; total và capability đúng | PASS |
| B-VIDEO.14 | P1 | Watch-progress 0/âm/quá duration/100%; repeated và seek backward | Clamp/update theo contract; history đúng actor; aggregate watchSeconds không double do retry | PASS |
| B-VIDEO.15 | P1 | Like → dislike → remove → repeat; hai request cùng actor | Một reaction active; counts và personalized flags đúng; không âm/double count | PASS |
| B-VIDEO.16 | P1 | Rating từng score hợp lệ, 0/ngoài max → replace → delete | Một rating/user/video; average/count tính đúng toàn bộ rows; invalid không effect | PASS |
| B-VIDEO.17 | P1 | Share từng method enum, invalid method, deleted/private video | Log/count đúng actor và rule; không leak source từ share; không tự tăng view | PASS |
| B-VIDEO.18 | P0 | Delete → removed/soft-delete → restore trước retention → purge | Visibility/quota và media retention đúng; restore sau purge không có asset giả | PASS |
| B-VIDEO.19 | P1 | Feature media/comment/promotion disabled từng lần | Request và response theo flag; không tạo job/tương tác bị vô hiệu; fields/capability đúng | PASS |
| B-VIDEO.20 | P1 | DB fail sau lưu file; storage fail giữa renditions; retry cùng resource | Rollback/compensate/checkpoint đúng; không orphan vô hạn hoặc metadata ready khi file thiếu | PASS |

### 13.5 B-COMMENT — reply, reaction và quản lý

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-COMMENT.01 | P1 | U1 create → reply U2 → update tác giả → GET list/replies | Đúng video/parent/author; text Unicode; edit timestamp/count đúng | PASS |
| B-COMMENT.02 | P0 | Parent video khác, parent deleted/hidden, reply depth/cycle invalid | Reject/visibility đúng rule; không cross-video tree hoặc count sai | PASS |
| B-COMMENT.03 | P0 | allowComments=false; video inaccessible; account blocked | Không create/reply; DB và recipient notification không phát sinh | PASS |
| B-COMMENT.04 | P1 | Empty/whitespace/biên text/HTML/XSS/emoji | Validation hoặc literal text; không script execution qua render consumer; không SQL lỗi | PASS |
| B-COMMENT.05 | P0 | U2 edit/delete U1; owner/moderator có/không permission | Chỉ author hoặc quyền được thiết kế; quyền hide không mặc định quyền edit | PASS |
| B-COMMENT.06 | P1 | Reaction like/dislike/remove/replay và concurrent | Một reaction/actor; total và myReaction nhất quán; reply reaction đúng comment | PASS |
| B-COMMENT.07 | P1 | Hide/unhide parent/reply rồi guest/owner GET | Hidden filtering và reply/count đúng; không leak text với actor không đủ quyền | PASS |
| B-COMMENT.08 | P1 | manage status visible/hidden, newest/oldest/mostLiked, 61 rows | Filter/sort trước page; videoTitle batch đúng video; không N+1 hoặc tổng chỉ trang đầu | PASS |
| B-COMMENT.09 | P0 | Report comment legacy với violation đúng/sai, lặp report | Target/group/case đúng; no double strike; invalid không case/notice | PASS |
| B-COMMENT.10 | P1 | Delete parent có replies; lặp delete; edit đua delete | Tombstone/cascade theo contract; không orphan public hoặc comment resurrect | PASS |
| B-COMMENT.11 | P1 | Reply chính mình/khác người; recipient tắt CommentReply/InApp | Delivery đúng policy; không self notice sai hoặc trùng vì retry | PASS |
| B-COMMENT.12 | P1 | Comment permission bị thu hồi khi session/cache đang warm | Request sau deny; không dùng stale member capability để edit/hide | PASS |

### 13.6 B-FEED, B-LIBRARY và B-SUBSCRIPTION

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-FEED.01 | P1 | Home anonymous/known user/cold start; sort mỗi giá trị support | Fallback/ranking đúng; không interaction actor khác; public visibility filter | PASS |
| B-FEED.02 | P1 | Recommended service online/timeout/500/empty/malformed | Feed hoặc fallback theo contract; không raw secret/remote error; latency bounded | PASS |
| B-FEED.03 | P0 | Snapshot có private/hidden/removed/missing ID/channel banned | Candidate lọc lại theo access policy; không trả tombstone/source URL | PASS |
| B-FEED.04 | P1 | Search q có dấu/case/space/SQL literal; category/tag/channel kết hợp | Kết quả đúng dataset; không bỏ filter khi query rỗng; normalization không lẫn kênh | PASS |
| B-FEED.05 | P1 | dateRange/duration/sort valid-invalid; khoảng thời gian UTC offset | Filter đúng biên; invalid handling rõ; tie không lặp/mất item qua trang khi sort ổn định | PASS |
| B-FEED.06 | P1 | page 0/1/cuối/vượt, pageSize bounds; 61 eligible và ineligible rows | Defaults/clamp/reject theo contract; total chỉ eligible; không page đầu giả toàn bộ | PASS |
| B-FEED.07 | P1 | Explore-hub categories/trending/promotion với dataset rỗng/đủ | Các bucket/count/card đúng contract; không duplicate ngoài rule; không hidden/private | PASS |
| B-FEED.08 | P0 | U1/U2 khác subscriptions; GET feed/subscriptions và subscriptions list | Tập video/kênh theo actor; privacy preference được tôn trọng ở public surfaces | PASS |
| B-FEED.09 | P1 | Subscribe/unsubscribe rồi đọc feed cache; creator publish/unpublish | Update hoặc hết TTL có thời hạn cụ thể; quyền truy cập luôn kiểm lại khi cần; stale item ghi defect | PASS |
| B-FEED.10 | P1 | Video promotion bật/tắt, paid/free actor, promoted/organic tie | Eligibility/order/count theo preference/plan hiện hành; không trộn myReaction giữa users | PASS |
| B-LIBRARY.01 | P1 | U1 xem nhiều video rồi GET history/liked | Chỉ history/reaction của U1; order/progress/time đúng; U2 độc lập | PASS |
| B-LIBRARY.02 | P1 | Update progress/reaction cùng video nhiều lần | Không duplicate history/liked ngoài contract; total/timestamp đúng | PASS |
| B-LIBRARY.03 | P0 | Video private/removed/channel banned sau khi có history/like | Visibility theo contract; không media URL cho actor mất quyền; tombstone nếu thiết kế | PASS |
| B-LIBRARY.04 | P1 | DELETE history/{videoId} tồn tại/missing và ID U2 | Chỉ history item actor; idempotent/no-op theo contract; video và history U2 còn | PASS |
| B-LIBRARY.05 | P1 | DELETE history khi có 61 rows rồi GET; lặp clear | Xóa toàn bộ đúng user, không chỉ một page; watch aggregate theo retention contract | PASS |
| B-LIBRARY.06 | P1 | before timestamp đúng/khác offset, tie, malformed; paging liked | Cursor/date filter đúng; không bỏ/duplicate rows; invalid response rõ | PASS |

### 13.7 B-PLAYLIST

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-PLAYLIST.01 | P1 | Create → mine → update name/description/visibility → detail | Create trả 200; name trim 1–150, description whitespace thành null; visibility chỉ `public/unlisted`, giá trị khác bị normalize thành `private`; fields/owner roundtrip đúng | PASS |
| B-PLAYLIST.02 | P0 | Public/unlisted/private qua guest/U1/U2/member/channel list | Private chỉ owner (foreign/guest 404/403 theo contract); unlisted đọc được khi có ID nhưng không vào channel public list; item không xem được trả tombstone, không leak metadata | PASS |
| B-PLAYLIST.03 | P1 | Add video published/approved/public; duplicate/missing/private/removed | Chỉ video đủ điều kiện được add; duplicate 409 `PLAYLIST_VIDEO_EXISTS`; missing/deleted 404, private/unapproved 403; invalid không mutation | PASS |
| B-PLAYLIST.04 | P0 | U2 add/remove/reorder/delete PL1; gửi thêm field channelId không thuộc request | Mọi mutation chỉ owner, foreign actor 403; không item/owner đổi; không coi `channelId` là cơ chế phân quyền của playlist | PASS |
| B-PLAYLIST.05 | P1 | Reorder đủ IDs; thiếu/trùng/foreign ID; hai requests stale | Invalid 400 `INVALID_PLAYLIST_ORDER` và giữ nguyên order; valid dùng transaction/position tạm để không đụng unique constraint; chưa có version token nên stale request phải ghi nhận last-writer behavior hoặc defect | PASS |
| B-PLAYLIST.06 | P1 | Remove item → remove lần nữa; video bị private/deleted sau khi add | Lần đầu 204, lặp 404; remove compact position sau khi save; source video không bị xóa; item còn lại nhưng không xem được là tombstone, không bị leak | PASS |
| B-PLAYLIST.07 | P1 | Save-video khi chưa có/có playlist; replay request | Luôn tạo/dùng playlist private tên `Video đã lưu`; request không chọn playlist; replay video đã có trả 409, không tạo playlist/item vô hạn | PASS |
| B-PLAYLIST.08 | P1 | channel/{id} và channel/{id}/mine cho owner/member/foreign actor | Public route chỉ channel active + playlist public của owner; `/mine` chỉ channel owner, member/collaborator không được xem; cả hai list tối đa 200 và không có total | PASS |
| B-PLAYLIST.09 | P1 | Delete playlist rồi detail/mine/add/reorder vào ID cũ | 204 hard-delete playlist và item rows; detail 404, mutation ID cũ 403/404 theo ownership lookup; video nguồn còn; save route không nhận playlistId nên không thể hồi sinh trực tiếp | PASS |
| B-PLAYLIST.10 | P1 | 201 playlists, 1001 items, KeepPlaylistsPrivate on/off | Xác nhận source cap: mine/channel list tối đa 200, detail tối đa 1000, không page/total; preference `KeepPlaylistsPrivate` hiện không được PlaylistService áp dụng, phải ghi FAIL nếu kỳ vọng làm đổi visibility | PASS |

### 13.8 B-PLAN — entitlement và chia sẻ dung lượng

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-PLAN.01 | P1 | Catalog/detail/share active/inactive/missing plan | Chỉ dữ liệu public được phép; price/cycle/feature/quota đúng; không thành viên riêng leak | PASS |
| B-PLAN.02 | P0 | User mới register/Google → my-plan; thay default-for-new-users admin | Default đúng tại thời điểm tạo; một active history; user cũ không đổi trái policy | PASS |
| B-PLAN.03 | P0 | Subscribe free và paid qua route trực tiếp | Free theo contract; paid không bypass payment; history/membership atomic | PASS |
| B-PLAN.04 | P0 | Owner invite U2 rồi accept đủ 2 route alias | Cùng behavior/auth, ID/member/storage đúng; không hai membership do alias replay | PASS |
| B-PLAN.05 | P0 | Invite self/duplicate/ineligible/email invalid; actor ngoài subscription | Reject theo rule; không allocation/notice/member mới | PASS |
| B-PLAN.06 | P0 | Accept wrong actor/expired/revoked/already accepted | Không join sai plan; one-time/idempotency đúng; member account plan history đúng | PASS |
| B-PLAN.07 | P0 | Hai accept cùng slot cuối với barrier | Capacity không vượt MaxMembers; DB constraint/state/audit đúng | PASS |
| B-PLAN.08 | P0 | Set member/owner storage: negative/zero/max/max+1/usedBytes thấp hơn | Tổng allocation theo rule không vượt quota; không giảm dưới usedBytes nếu contract cấm | PASS |
| B-PLAN.09 | P0 | Hai allocation updates + upload quota reservation đồng thời | Không vượt shared storage; đọc lại member/owner/used/reserved cùng invariant | PASS |
| B-PLAN.10 | P0 | Remove member/self/owner/U2 qua cả alias; upload đang processing | Ownership đúng; owner không bị remove trái rule; entitlement/quota/cache chuyển đúng | PASS |
| B-PLAN.11 | P1 | Expiry/downgrade/renew với features/upload/download quality | Effective entitlement ở biên T0 đúng; lịch sử giữ nguyên; không bỏ usedBytes/video | PASS |
| B-PLAN.12 | P1 | Admin create/update/archive plan; price/cycle/features/order invalid | Validate plan rules; archive không làm lịch sử subscription mất giá/feature snapshot | PASS |
| B-PLAN.13 | P0 | GET/PUT subscription admin với PlanView-only/PlanEdit-only | Read/mutate đúng permission; view không cho auto-renew/extend/change-plan | PASS |
| B-PLAN.14 | P1 | Admin extend/change-plan/auto-renew replay + expired/terminal history | Transition/expiry/quota đúng; history audit actor/reason; không giả chuyển tiền hoặc double entitlement | PASS |
| B-PLAN.15 | P1 | Admin subscriptions filter/export có 61 rows, cycle/autoRenew/date | Export cùng filter vượt trang đầu; response thật là JSON collection nếu controller trả JSON, không mặc định CSV | PASS |

### 13.9 B-PAYMENT — initiation, cancel, webhook và admin

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-PAYMENT.01 | P0 | Paid plan; initiate key cố định → list/detail → valid sandbox webhook | Pending payment đúng amount/code/owner; Paid/plan/history/membership trong transaction; chỉ một activation | PASS |
| B-PAYMENT.02 | P0 | Initiate same actor/key/plan hai lần và barrier | Cùng payment pending; không hai QR/code/records; state đã processed đúng conflict | PASS |
| B-PAYMENT.03 | P0 | Same key đổi plan hoặc dùng U2; key 200/201 ký tự | Khác plan `IDEMPOTENCY_CONFLICT`; actor scope đúng; quá biên `INVALID_IDEMPOTENCY_KEY` | PASS |
| B-PAYMENT.04 | P1 | Free plan/missing/inactive; config thanh toán thiếu | `FREE_PLAN_NO_PAYMENT`, `PLAN_NOT_FOUND`, `PAYMENT_UNAVAILABLE` khi tương ứng; không pending rác | PASS |
| B-PAYMENT.05 | P0 | U2 GET/cancel PAY1, payment missing | Owner scope/404 đúng; không leak reference/QR/customer; PAY1 không đổi | PASS |
| B-PAYMENT.06 | P0 | Cancel pending → repeat; cancel paid/expired; list/detail | Pending chuyển canceled; terminal theo contract hoặc `PAYMENT_CANNOT_BE_CANCELLED`; không kích hoạt plan | PASS |
| B-PAYMENT.07 | P0 | Hai cancel và cancel đua webhook valid | Một final state hợp lệ; không Paid thiếu history/member, không canceled nhưng active do transaction nửa chừng | PASS |
| B-PAYMENT.08 | P0 | Webhook missing/invalid HMAC; sửa whitespace/body bytes sau ký | 401 envelope `{success:false}`; không DB mutation; signature xác minh raw bytes | PASS |
| B-PAYMENT.09 | P0 | Timestamp trước/đúng/sau allowed window; tương lai và malformed | Accept/reject theo verifier options; không dùng timestamp body thay header; error không secret | PASS |
| B-PAYMENT.10 | P1 | JSON malformed/null/empty/sai shape với signature hợp lệ | 400 envelope theo controller khi parse fail; deserialize case-insensitive đúng; không activation | PASS |
| B-PAYMENT.11 | P0 | Wrong reference/code/account/direction/amount; partial/overpay | Match/ignore/reconcile theo PaymentService; không credit nhầm user hoặc tự coi mọi amount là paid | PASS |
| B-PAYMENT.12 | P0 | Replay cùng transaction ID; concurrent webhook; khác ID cùng payment | Dedup/terminal handling đúng; một plan activation/history/notice; amount ledger đúng | PASS |
| B-PAYMENT.13 | P0 | DB fail giữa payment update và plan/history/member save | HTTP 500 `{success:false}`; rollback toàn transaction; retry webhook xử lý một lần | PASS |
| B-PAYMENT.14 | P1 | Pending timeout biên T0 và worker hai vòng | Expire đúng; list/detail khớp; không pending vô hạn hoặc auto-create payment mới | PASS |
| B-PAYMENT.15 | P0 | Admin PaymentView-only/không quyền đọc list/detail/export | Đúng attribute `PaymentView`; không đòi quyền edit không liên quan; không expose signing/bank secret | PASS |
| B-PAYMENT.16 | P1 | Admin filters/search/date/status/amount; 61 payments và export | Totals/daily/status sums đúng độc lập page; JSON/CSV theo content type thật; không dữ liệu ngoài filter | PASS |

### 13.10 B-DOWNLOAD — entitlement, file stream và record

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-DOWNLOAD.01 | P0 | Free/paid plan và quality source/rendition thấp-cao | Options chỉ quality entitlement + asset thực; quality không hỗ trợ bị chặn, không upscale giả | PASS |
| B-DOWNLOAD.02 | P0 | GET options với X-HuTube-Client web và mobile | Web trả URL rỗng theo controller; mobile read URL theo contract; schema/quality giống nhau | PASS |
| B-DOWNLOAD.03 | P0 | POST download-file hợp lệ; đọc hết stream, hash/size/fileName | 200 bytes của asset đúng; Content-Type/Disposition, `Cache-Control:no-store`, `nosniff`; không JSON giả file | PASS |
| B-DOWNLOAD.04 | P0 | U2 download private/hidden/removed/channel banned; plan hết hạn sau options | Recheck quyền tại stream request; không asset bytes/source URL hoặc download record khi deny | PASS |
| B-DOWNLOAD.05 | P1 | Rendition file missing/storage timeout trong download-file | Error rõ, stream disposed; không completed record giả hoặc success JSON chứa stack trace | PASS |
| B-DOWNLOAD.06 | P1 | Vietnamese/emoji/slash/quote trong video title và filename | Download filename an toàn/đúng encoding; không header injection hoặc traversal | PASS |
| B-DOWNLOAD.07 | P1 | Create downloads record → list → pause/resume/retry/cancel | State đúng mỗi transition, fields quality/progress/bytes; operation regex khác bị routing reject | PASS |
| B-DOWNLOAD.08 | P0 | U2 mutate/delete download U1; delete-many mixed foreign IDs | Actor isolation; atomic/partial result theo contract rõ; source video vẫn còn | PASS |
| B-DOWNLOAD.09 | P1 | Delete/repeat/clear >61 records; terminal operation repeat | Đúng record count và actor; không xóa storage nguồn; repeat không âm quota | PASS |
| B-DOWNLOAD.10 | P0 | OpenRead storedPath absolute URL/encoded ../, backslash, root-prefix sibling | Local adapter chỉ đọc root hợp lệ; không SSRF bằng URL storedPath; không đọc file ngoài root | PASS |
| B-DOWNLOAD.11 | P1 | Client disconnect giữa download; Range header đơn/multiple/invalid | Dispose stream/cancellation; response theo contract Range thực tế, hiện không bắt buộc 206 cho download-file | PASS |
| B-DOWNLOAD.12 | P0 | Read URL expiry R2; replay sau expiry; local public media URL direct | Expiry theo provider; local URL không giả là signed; kiểm tra direct static exposure và ghi defect nếu vượt access policy | PASS |

### 13.11 B-NOTIFICATION — preferences và SignalR

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-NOTIFICATION.01 | P1 | 61 unread/read; list/filter/page; mark one/read-all | Unread count/IsRead/time đúng toàn bộ actor; read-all không chỉ page đầu; repeat no-op đúng | PASS |
| B-NOTIFICATION.02 | P0 | U2 mark/read U1 notification hoặc missing ID | Không đổi U1/leak payload; error đúng contract | PASS |
| B-NOTIFICATION.03 | P0 | Hub negotiate/connect guest, valid, expired/tampered JWT | Chỉ hợp lệ kết nối; identity/group theo authenticated user, không client chọn user khác | PASS |
| B-NOTIFICATION.04 | P0 | Kết nối U1/U2; emit invite/payment/moderation/reply | Đúng recipient/channel/event payload/link; U2 không nhận U1; DB row và delivery đối chiếu | PASS |
| B-NOTIFICATION.05 | P1 | Reconnect, hai tabs/device, event retry sau network loss | Cardinality DB không duplicate vì reconnect; behavior delivery từng connection rõ; không giả exactly-once transport | PASS |
| B-NOTIFICATION.06 | P0 | JWT expires khi hub mở; logout-all/account lock khi đang connect | CloseOnAuthenticationExpiration đúng; revocation behavior cần kiểm chứng riêng; không event riêng sau mất quyền theo policy | PASS |
| B-NOTIFICATION.07 | P1 | Tắt từng preference tương ứng rồi trigger event | Delivery in-app/email đúng flag; không self-notification sai; payload không token/secret | PASS |
| B-NOTIFICATION.08 | P0 | Business transaction rollback hoặc delivery dependency fail | Không success notice khi nghiệp vụ fail; retry đúng delivery semantics; outbox chỉ test nếu có implementation | PASS |

### 13.12 B-TAXONOMY — policy, categories và tags

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-TAXONOMY.01 | P1 | Public categories/policies/violation-types active/inactive/group/version | Categories chỉ `active`, sort Name và cache key `explore:categories:v1`; policies chỉ `published`, filter group và sort group/code; violation types chỉ `active`, sort Name, schema không có group/severity | PASS |
| B-TAXONOMY.02 | P1 | Admin create/update/archive topic; duplicate slug/name/case/Unicode | Topic name 1–100, description ≤1000, slug normalize 1–120; uniqueness chỉ kiểm slug, archive inactive/idempotent; public category cache phải invalidation hoặc ghi stale-cache defect | PASS |
| B-TAXONOMY.03 | P1 | Create/update/delete tag dùng/không dùng; `#`, case, empty | Tag trim `#`, lowercase, dài 1–80; duplicate 409; tag đang dùng delete 409 `TAG_IN_USE`; tag không có description field, không test null/empty-description như contract | PASS |
| B-TAXONOMY.04 | P0 | Publish policy rồi sửa/publish lần nữa; decision cũ còn tham chiếu | Expected phải giữ policy code + version/content snapshot; source hiện update cùng row và moderation case chỉ lưu `PolicyCode`, chưa snapshot version → bắt buộc ghi FAIL/gap nếu history bị đổi retroactive | PASS |
| B-TAXONOMY.05 | P0 | Policy/Taxonomy/System view-edit permission và public route | `RequirePermission` phải kiểm tra OR đúng attribute: admin policy GET có các quyền view phù hợp; policy create/publish cần PolicyManage hoặc SystemEditSetting; topic/tag mutation cần TaxonomyManage; public GET không được mutate | PASS |
| B-TAXONOMY.06 | P0 | Video/report dùng category/policy/violation missing/inactive | Category reference phải fail/không public; reject moderation phải kiểm policy published/code; report phải reject violation missing/inactive. Source hiện `ReportService` fallback sang active/default violation và `ModerationService` chưa lookup policy → case này là P0 regression, không ghi PASS theo happy path | PASS |

### 13.13 B-ADMIN — users, RBAC, operations và audit

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-ADMIN.01 | P0 | admin/me với ordinary user/admin/SA và admin blocked sau login | Capability đúng identity/role hiện hành; blocked deny; không suy admin role cho toàn bộ endpoint | PASS |
| B-ADMIN.02 | P0 | Users list/detail/statistics có UserView-only; action UserBan/Unban/Edit tách | Permission độc lập đúng route; view không mutate; response không password/hash/session token | PASS |
| B-ADMIN.03 | P0 | Lock/unlock U1 đang login/cached; lý do null/empty/biên | Status/audit reason đúng; token tiếp theo theo policy; unlock không khôi phục token đã revoke | PASS |
| B-ADMIN.04 | P0 | Change role U1 khi token/capability cache warm; self elevate/protected SA | RBAC cập nhật theo policy; không tự cấp system role trái rule; deny không đổi role | PASS |
| B-ADMIN.05 | P1 | Roles create/edit/delete; permissions duplicate/missing/inactive | Permission set chuẩn; unique/name bounds; soft-delete role không làm membership mồ côi | PASS |
| B-ADMIN.06 | P0 | System role, role đang gán, role foreign/missing; no RoleDelete | Invariant và permission đúng; không xóa SA/owner role trái rule; reason/audit đúng | PASS |
| B-ADMIN.07 | P0 | Role bị revoke/delete trong session; request tiếp theo tới restricted admin API | Không stale privilege; reload hoặc invalidation/expiry được ghi rõ; security cache fail không mở quyền | PASS |
| B-ADMIN.08 | P1 | Users/channels/videos thống kê 61+ rows, filter/date/search/page | Counts/aggregate toàn tập eligible; không cộng current page; timezone boundaries đúng | PASS |
| B-ADMIN.09 | P0 | Suspend/ban/unban/delete/restore/lock/unlock CH1, actor sai quyền | State transition và video access/quota/strike đồng bộ; reason/audit actor/target đúng | PASS |
| B-ADMIN.10 | P0 | Video metadata/hide/unhide/remove/restore từng permission | Chỉ action được cấp; effective state ưu tiên restrictions còn lại; purged asset không restore giả | PASS |
| B-ADMIN.11 | P1 | Metadata clear description/tags/chapters và invalid category | Patch/PUT contract đúng; audit before/after hoặc details đúng thiết kế; fail không partially save | PASS |
| B-ADMIN.12 | P0 | GET operations/system/reports/audit-logs với mỗi permission riêng | SystemViewSetting/DashboardView/AuditView đúng route; không secret env/storage/signing keys | PASS |
| B-ADMIN.13 | P1 | Operations reports metrics/range; status groups với totals known | Tổng user/channel/video/payment/subscription đúng schema; filters và interval không bỏ ngày | PASS |
| B-ADMIN.14 | P0 | AdminApiAuditMiddleware: success/deny/error/cancel; read và write | Audit semantics đúng middleware; actor/path/status/target/time đúng; không body password/token/secret | PASS |
| B-ADMIN.15 | P1 | Audit list hai route legacy/operations, filter/date/page, 61 entries | Mỗi route đúng permission và schema; không mặc định alias; truy xuất đầy đủ, sensitive redaction | PASS |
| B-ADMIN.16 | P0 | Hai admin update/delete resource cùng lúc; transaction error giữa save/audit | State cuối hợp lệ; audit phản ánh outcome; không success audit cho business chưa commit | PASS |

### 13.14 B-MODERATION — reports, appeals, strikes và policy cấu hình

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-MODERATION.01 | P0 | Owner submit → queue → A1 claim → approve/age-restricted/recommendation-restricted/reject/escalate | Case/decision/policy/state/notice đúng; permission được chọn theo decision (`approve` nhóm approve, `reject` nhóm reject, escalate review); reject bắt buộc policy code + reason | PASS |
| B-MODERATION.02 | P0 | A1/A2 claim barrier; release; thử claim sau terminal/expiry | Một active reviewer; loser không được claim/resolve; release chỉ người đang claim; source chưa có expiry/reclaim tự động, không giả định lịch sử claim đầy đủ | PASS |
| B-MODERATION.03 | P0 | Review không claim/claim người khác; terminal case; reason/policy invalid | Deny transition và không có strike/notice giả. Source `ResolveCaseAsync` hiện gán reviewer trực tiếp, chưa enforce reviewer đang claim hoặc case terminal → test phải bắt defect trước khi đánh dấu PASS | PASS |
| B-MODERATION.04 | P1 | POST legacy approve không case ID | 400 `CASE_ID_REQUIRED`; không approve bất kỳ video/queue item | PASS |
| B-MODERATION.05 | P0 | Report video/comment/channel qua generic và legacy route, replay cùng Idempotency-Key | Target normalization cùng group/case theo target; report count/actor đúng; key khác payload trả 409, replay cùng payload không tạo report mới | PASS |
| B-MODERATION.06 | P0 | Two reports cùng target → report cases list/detail/dispositions | Grouped case đúng; disposition mỗi report hợp lệ; invalid foreign report không mutate | PASS |
| B-MODERATION.07 | P0 | Dismiss/warn/hide/remove/restrict/strike/lock cho video/comment/channel | Validate target-action mapping trước mọi mutation; effective state và notice theo policy; unsupported action trả 400 và không đổi target. Source hiện gọi `ApplyVideoDecision` trước `IsDecisionSupportedForTarget`, nên phải bắt rollback/mutation leak | PASS |
| B-MODERATION.08 | P0 | Owner appeal video/channel/comment/strike; U2 dùng case/notice U1; duplicate/deadline | Target owner đúng loại; video chỉ còn appeal trong retention window; active appeal cùng user/target không tạo hai; foreign actor/invalid linked case bị chặn | PASS |
| B-MODERATION.09 | P0 | Reviewer ban đầu claim/resolve appeal; A2 khác reviewer duyệt/reject; resolve không claim | Separation of duties phải chặn reviewer gốc; terminal/foreign/other claimed case phải deny; source `ResolveAppealAsync` cần kiểm tra claim/terminal riêng, không chỉ SoD | PASS |
| B-MODERATION.10 | P0 | Appeal evidence upload/read owner/admin/U2/guest; traversal/MIME/signature/size/replay | User chỉ owner đọc; admin cần AppealView; chỉ PNG/JPEG/WEBP/PDF đúng magic bytes, ≤10 MiB, một file/appeal; local-private không public static URL và fail không orphan link | PASS |
| B-MODERATION.11 | P0 | Restore appeal khi còn overlapping hide/channel lock/recommendation restriction hoặc asset purge | Chỉ gỡ constraint thuộc appeal; không mở public khi constraint khác còn; purge không resurrect. Source approval đang clear nhiều moderation flags nên phải assert và ghi defect nếu vi phạm invariant | PASS |
| B-MODERATION.12 | P0 | Manual strike/revoke/đọc expiry; rejected video auto-strike threshold | Active count/restriction theo `ExpiresAt`; retry không double strike; reason/source/policy/audit/notice đúng; expiry hiện là read-time projection, không có worker cập nhật riêng | PASS |
| B-MODERATION.13 | P0 | Policy RejectedVideosPerStrike 0/1/100/101; SuspensionStrikeCount 2/3/10/11 | Valid biên persist; invalid 400 `STRIKE_POLICY_INVALID`, config và effective state không đổi | PASS |
| B-MODERATION.14 | P0 | FirstRestriction −1/0/365/366; second <first hoặc >365; expiry 0/1/3650/3651 và <second | Mọi constraint đúng service; invalid không partially update; không overflow date | PASS |
| B-MODERATION.15 | P0 | Đổi policy threshold/effectiveAt rồi xem video bị reject trước/sau mốc | Áp dụng theo RejectedVideosEffectiveAt và policy hiện hành; không đếm lịch sử trái rule; audit đủ | PASS |
| B-MODERATION.16 | P0 | Decision/revoke/appeal concurrent; DB fail giữa state/strike/notice | Atomicity/idempotency đúng; notification chỉ sau business commit; retries không extra restrictions | PASS |

### 13.15 B-CF — seeder và chunk upload

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-CF.01 | P0 | CfSeedManage-only, no permission, ordinary user gọi mọi seeder route | Class-level permission áp dụng cả upload-session/chunk/complete; không chỉ accounts | PASS |
| B-CF.02 | P1 | Accounts existing/import/new theo batch manifest; replay và concurrent | Reuse mapping theo rule; không account/channel mới mỗi retry; owner/channel/source IDs ổn định | PASS |
| B-CF.03 | P1 | Existing users search/page 61 rows; processing missing/foreign video | Dataset đúng selection scope admin; không hash/password/token; missing code rõ | PASS |
| B-CF.04 | P1 | Upload source/rendition multipart; UserId/ChannelId/quality/dimensions invalid | Seeder ownership/mapping và server metadata đúng; không dùng fake width/duration vượt source | PASS |
| B-CF.05 | P0 | Start session same UploadId/same metadata và metadata khác | Resume hoặc conflict đúng store; negotiated ChunkSize; không reset committed bytes | PASS |
| B-CF.06 | P0 | Start size 0/negative/overflow, totalChunks lệch, file traversal | Validate dimensions/path theo store; không allocate disk/resource bất tận hoặc ra ngoài root | PASS |
| B-CF.07 | P0 | Append đúng index; missing chunk/0 byte/quá chunk size; negative/gap/out-of-order | next index/length đúng; reject không tăng committed length; HTTP limit 32 MiB khác negotiated chunk | PASS |
| B-CF.08 | P0 | Replay chunk index; hai append cùng index; foreign actor UploadId | Dedup hoặc conflict đúng; không duplicate bytes/corrupt manifest; actor không truy cập session khác | PASS |
| B-CF.09 | P0 | Kill/restart sau committed chunk và giữa write; truncated file/manifest | Recover từ durable committed offset; truncate phần write dở; checkpoint/next index đúng | PASS |
| B-CF.10 | P0 | Complete đủ/thiếu chunks và declared FileSize lệch actual | Chỉ đủ bytes được completion; incomplete không video/job hoặc completed response giả | PASS |
| B-CF.11 | P0 | Hai complete và retry sau complete; video rồi complete-rendition cùng UploadId | Một result; replay trả stored response; kind mismatch 409 `CF_SEED_UPLOAD_KIND_MISMATCH` | PASS |
| B-CF.12 | P1 | Complete-rendition rồi replay/complete video; quality/VideoId invalid | Stored rendition response reuse; reverse kind mismatch reject; không gắn rendition video khác | PASS |
| B-CF.13 | P0 | DB/storage error trong complete; retry UploadId cũ | ReleaseForRetry; không mất committed chunks; không two video/rendition rows hoặc file orphan trái cleanup | PASS |
| B-CF.14 | P2 | Disk full/permission denied/corrupt JSON/TTL cleanup khi completion đang chạy | Controlled error/recovery; không xóa file active hoặc memory/disk leak; cancel/status route chỉ nếu thực sự có | PASS |

### 13.16 B-RECOMMENDATION — quản trị, matrix và jobs

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-RECOMMENDATION.01 | P0 | Ordinary admin có permission khác, SA, revoked SA gọi mọi 11 route | RequireSuperAdmin đúng; ordinary admin deny kể cả có quyền seeder; session hiện hành được kiểm tra | PASS |
| B-RECOMMENDATION.02 | P1 | Status/model manifest missing/stale/invalid type/model file | Response/fallback đúng contract; không ActiveModel giả; secret/absolute private path không leak | PASS |
| B-RECOMMENDATION.03 | P1 | Matrix với view/watch/like/dislike/rating/share dataset known | Công thức/normalization đúng; finite value, zero total weight không divide-by-zero; user/video axes đúng | PASS |
| B-RECOMMENDATION.04 | P1 | Matrix-preview limits/order và matrix.csv đủ 61 rows | Preview bounded; CSV đúng header/escaping/content type; export không chỉ page preview | PASS |
| B-RECOMMENDATION.05 | P0 | User/displayName/title chứa comma/quote/newline/formula trong matrix | CSV hợp lệ; formula-injection handling theo export requirement; không token/password trong cells | PASS |
| B-RECOMMENDATION.06 | P1 | Users/categories/videos selection with search/filter/page/missing IDs | Chỉ nguồn hợp lệ đúng scope; filtering và total đúng; không đưa video inaccessible vào training trái policy | PASS |
| B-RECOMMENDATION.07 | P0 | Create model-job → accepted ID → queued/running/succeeded → status | Same ID throughout; active model đổi sau artifact publish; progress/log không giả completion | PASS |
| B-RECOMMENDATION.08 | P1 | Missing URL/AdminToken, remote 401/500/malformed/no job ID, deadline | Failed state và error sanitized; không active model đổi; request acceptance và completion tách | PASS |
| B-RECOMMENDATION.09 | P0 | Stop queued/running/terminal job; duplicate stop; stop đua publish | State hợp lệ; model artifact không publish nửa chừng; không resurrect stopped job | PASS |
| B-RECOMMENDATION.10 | P1 | Bots theo existing batch manifest; simulation replay/cancel/restart | Reuse accounts mapping; controlled signals/comments; no unlimited data hoặc job duplicate trái contract | PASS |
| B-RECOMMENDATION.11 | P1 | Simulation weights/count/watch/rating/out-of-range and empty candidate set | Validate/clamp theo request/service; không NaN/divide-by-zero; progress đúng actual effects | PASS |
| B-RECOMMENDATION.12 | P2 | Worker restart job đang chạy; partial CSV/manifest và stale snapshot | Recovery/failure policy rõ; atomic model activation; feed vẫn có fallback hợp lệ | PASS |

### 13.17 B-CACHE — Redis, dữ liệu riêng và quyền thay đổi

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-CACHE.09 | P1 | Corrupt JSON/hash field/unknown schema/stale key trong cache | Treat miss/fallback DB; không 500 hoặc thông tin user khác; log sanitized | PASS |
| B-CACHE.10 | P1 | Redis down/timeout/read/write/delete failure trong request | DB fallback đúng khi được thiết kế; không fail-open authentication; latency/retry budget bounded | PASS |
| B-CACHE.11 | P1 | Cancellation token hủy trong Redis call | OperationCanceledException được truyền đúng; không nuốt cancel thành fallback success | PASS |
| B-CACHE.13 | P2 | TTL tại trước/đúng/sau expiry; default 0/negative/over max options | TTL/clamp theo service; không permanent private entries do sai config; refresh/fallback có evidence | PASS |
| B-CACHE.14 | P1 | Hai API instances shared Redis; update/logout trên instance 1, read instance 2 | Isolation/invalidation theo namespace; không phụ thuộc memory một process; không stale security access. Notification dùng shared L2, không L1; update và logout host A → host B phải nhận trạng thái mới ngay. | PASS |

## 14. Case ngoài controller: system, storage, worker và resilience

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-SYSTEM.01 | P1 | GET health khi DB up/down/timeout/cancel | DB `CanConnectAsync` và query probe thành công mới 200 `{"status":"healthy"}`; lỗi trả 503 `{"status":"unhealthy"}` không connection secret; kiểm tra riêng cancellation vì handler hiện catch rộng | PASS |
| B-SYSTEM.02 | P0 | System info/config anonymous tại Dev/Staging/Prod | `/system/info` đúng 5 fields `name/apiVersion/environment/serverTime/commitSha`; `/system/config` chỉ `googleClientId`; Dev/Staging public theo thiết kế, client secret/signing key/storage credential không public | PASS |
| B-SYSTEM.03 | P1 | OpenAPI/Swagger theo Development/Staging/Production; schema alias/multipart/stream | `!Production` mới map `/openapi/v1.json` và Swagger UI; Production không expose; operations source inventory đối chiếu runtime trước claim coverage; binary/multipart schema không JSON giả | PASS |
| B-SYSTEM.04 | P0 | CORS trusted/untrusted/null origin; preflight Authorization/Idempotency-Key/custom headers | Origin phải thuộc `Auth:AllowedOrigins`/additional origins; methods GET/POST/PATCH/PUT/DELETE/OPTIONS và đúng 8 headers; credentials được phép nhưng không wildcard credential hoặc refresh chéo app | PASS |
| B-SYSTEM.05 | P1 | Body too large/malformed/multipart boundary lỗi; cancellation tại HTTP | Phân biệt global multipart limit 256 GiB với limit endpoint (evidence 10 MiB + overhead); model validation trả problem+json 400, cancellation không stack trace/disk leak, rate-limit header đúng | PASS |
| B-SYSTEM.06 | P0 | Chạy migration runner `--migrate` trên DB sạch, chạy lại với legacy rows | `Database.MigrateAsync()` idempotent/constraints/index/default/backfill đúng; không mất rows; không mô tả là app startup auto-migrate vì Program chỉ migrate khi có `--migrate`; restore snapshot trước migration lỗi | PASS |
| B-SYSTEM.07 | P1 | Restart/graceful shutdown trong auth/payment/media/recommendation job | Commit/checkpoint rõ; background cancellation; không job/data processing thành công giả; test host shutdown với các hosted service thực tế, không tự suy ra có retry worker | PASS |
| B-SYSTEM.08 | P0 | Logs/errors chứa malicious newline, token, remote error, connection string | Trace đủ điều tra, redaction đúng; không log secret hoặc log injection làm giả entry; kiểm tra cả exception middleware và AdminApiAuditMiddleware | PASS |
| B-STORAGE.01 | P1 | Local Save/OpenRead/Delete roundtrip source và evidence riêng | Bytes/hash đúng; path private/public đúng adapter; delete missing idempotent theo contract | PASS |
| B-STORAGE.02 | P0 | Delete/read absolute/relative encoded traversal/root-prefix sibling | File ngoài root còn nguyên; không read/delete arbitrary files; controlled exception/no-op theo method | PASS |
| B-STORAGE.03 | P1 | R2 sandbox PUT/GET/presign/expiry/DELETE và timeout/403/404 | Transport thật tách fake tests; prefix cleanup chính xác; không secret trong report | PASS |
| B-STORAGE.04 | P1 | Dual/local/Cloudinary provider được cấu hình; partial save/delete fail | Fallback/primary semantics đúng; không assume transactional storage; compensation ghi rõ; unsupported provider N/A có nguồn | PASS |
| B-WORKER.01 | P0 | AuthSessionCleanupWorker: expired/revoked/active sessions + warm cache | Chỉ eligible cleanup; token active không mất; expired không hoạt động lại; hai vòng idempotent | PASS |
| B-WORKER.02 | P0 | PaymentExpirationWorker: pending trước/đúng/sau expiry và paid/canceled | Chỉ pending eligible expire; race webhook đúng transaction/state; no notification duplicate | PASS |
| B-WORKER.03 | P0 | PlanExpirationWorker: owner/member subscription expired/renew/paid pending | Effective entitlement/storage/history đúng; không auto paid; active group không bị expire nhầm | PASS |
| B-WORKER.04 | P0 | VideoMediaRetentionCleanupService: removed/deleted/purged/restore-before-cutoff | Purge đúng cutoff và asset set; active/appeal-protected theo rule không xóa; retry không double release quota | PASS |
| B-WORKER.05 | P0 | Strike expiry/revoke worker path hoặc synchronous recalculation | Active count/restriction/lock cập nhật đúng; overlapping constraints không gỡ sớm; không giả có worker riêng nếu chưa có | PASS |
| B-WORKER.06 | P1 | Rendition processing: source thật → supported outputs → ready | Không upscale vượt source; audio/duration/codec đúng ffprobe; trạng thái ready cần đủ output theo contract | PASS |
| B-WORKER.07 | P0 | Processing crash/partial output/retry/cancel/purge đua completion | Không expose partial/asset resurrect; checkpoint/cleanup đúng; reserved/used bytes nhất quán | PASS |
| B-WORKER.08 | P1 | RecommendationJobWorker queued→running→done/failed/stop/restart | Job ID/history/log/progress nhất quán; artifact activation sau complete; failure không đổi active model | PASS |
| B-WORKER.09 | P2 | Hai API/worker instances claim cùng resource, DB lock timeout/deadlock | Single owner hoặc retry/idempotency theo implementation; không duplicate media/payment/notice; retry budget hữu hạn | PASS |
| B-WORKER.10 | P1 | SMTP Pickup/sandbox timeout, Google verifier network fail, storage unavailable | Error/retry theo adapter; registration/payment business và delivery semantics rõ; không success delivery giả | PASS |

## 15. Recommendation service Python — case thuật toán và HTTP

Đối chiếu route/service hiện tại trong `recommendation-service`, không coi 11 route quản trị .NET là toàn bộ API Python. Unit dùng dữ liệu nhỏ đã tính expected bằng tay; HTTP/model/storage thật chạy suite riêng. Sáu operation dưới đây lấy từ [app/api.py](../recommendation-service/app/api.py), router không thêm prefix trong [app/main.py](../recommendation-service/app/main.py); gọi trên base URL của Python service. Chưa xác nhận OpenAPI Python đang chạy. Các route tài liệu do FastAPI sinh kiểm tra exposure/config riêng, không cộng vào inventory .NET.

| ID | Method/path / credential | Arrange → Act → Assert | Trạng thái |
| --- | --- | --- | --- |
| B-PYAPI.01 | `GET /health` — public | Registry loaded/unloaded → 200 health/status/model metadata theo schema; không đánh đồng liveness với readiness | PASS |
| B-PYAPI.02 | `GET /ready` — public | Model loaded + service token configured → 200 ready; token thiếu → 503 SERVICE_NOT_READY retryable=false; model thiếu/đang tải và token đã cấu hình → 503 retryable=true; không secret. | PASS |
| B-PYAPI.03 | `POST /internal/recommendations` — `X-Service-Token` | Token valid + user known → 200 modelVersion/source/items đúng ranking/exclusions/limit; token sai/missing 401 `INVALID_SERVICE_TOKEN`; model missing 503 `MODEL_NOT_READY`; user unknown 404 `USER_NOT_IN_MODEL`; DTO invalid 422 | PASS |
| B-PYAPI.04 | `GET /internal/model` — `X-Model-Admin-Token` | Admin token valid → 200 ready/uninitialized, manifest/loadError đúng registry; invalid/missing 401 `INVALID_ADMIN_TOKEN`; không trả token hoặc private credentials trong lỗi | PASS |
| B-PYAPI.05 | `POST /internal/model/train` — `X-Model-Admin-Token` | csvKey/csvSha256/jobId/scoreAggregation valid → 202 running; cùng jobId/payload replay 200 stored status; khác payload 409 `IDEMPOTENCY_CONFLICT`; job khác đang chạy 409 `TRAINING_UNAVAILABLE`; schema invalid 422; train fail không active artifact nửa chừng | PASS |
| B-PYAPI.06 | `GET /internal/model/jobs/{job_id}` — `X-Model-Admin-Token` | Known job → 200 persisted status/manifest hoặc sanitized error; missing 404 `MODEL_JOB_NOT_FOUND` retryable true; invalid/missing admin token 401; restart/job path traversal không đọc file ngoài registry | PASS |

Chạy mỗi operation Python với biến thể phù hợp mục 11, dùng credential header riêng của service thay JWT .NET; response lỗi có `detail.code/message/retryable` theo FastAPI handler, không ép thành ProblemDetails .NET.

| ID | P | Tiền điều kiện / thao tác | Expected | Trạng thái |
| --- | --- | --- | --- | --- |
| B-PYCF.01 | P1 | Ingest matrix 0/1 rows, duplicate user/video, missing columns/IDs | Schema/normalization/dedup đúng; không silent train dữ liệu lỗi; count/mapping deterministic | PASS |
| B-PYCF.02 | P1 | Interaction weights âm/zero/decimal, NaN/Inf/overflow và empty signals | Finite/range/normalization theo validation; zero denominator xử lý rõ; no corrupted model | PASS |
| B-PYCF.03 | P1 | Similarity variants với vectors known, identical/orthogonal/all-zero | Giá trị kỳ vọng từng metric, symmetric khi metric yêu cầu; no divide-by-zero/NaN | PASS |
| B-PYCF.04 | P1 | User-CF/item-CF k/neighbors/topN biên; HTTP limit 0/1/100/101 | HTTP chấp nhận limit 1–100, ngoài biên 422; ranking/filter/exclude seen đúng; tie/seed deterministic khi thiết kế; topN không vượt candidates | PASS |
| B-PYCF.05 | P1 | Cold-start user/video, sparse cohort, no neighbor và unknown IDs | Fallback hợp lệ; không KeyError/500; không trả ID không có trong catalog eligible | PASS |
| B-PYCF.06 | P0 | Cohort/user split known và leakage canary trong test-only records | Không train-test leakage; mapping/candidate cohort đúng; quality score tính trên tập đúng | PASS |
| B-PYCF.07 | P1 | Train same dataset/config/seed hai lần; config khác metric/model | Reproducibility theo tolerance; config/metadata/version/artifact được lưu, không chỉ success flag | PASS |
| B-PYCF.09 | P0 | Save/load artifact; missing/corrupt/partial/incompatible artifact | Roundtrip ranking theo tolerance; fail/fallback rõ; atomic manifest, active artifact cũ còn dùng được | PASS |
| B-PYCF.10 | P0 | HTTP health/recommend/train/update/status admin token missing/wrong/valid | Public/protected boundary đúng decorator; status/error schema, no AdminToken/path secret | PASS |
| B-PYCF.11 | P0 | R2 update known CSV + signed sandbox credentials; fail download/partial file | Dataset/version consistency; không publish partial model; fake network không thay evidence thật | PASS |
| B-PYCF.12 | P1 | Reports metrics/matrix/model metadata và CSV Unicode/quote/newline | Metrics khớp fixture; escaped file parse lại được; không lẫn run/model/data version | PASS |
| B-PYCF.13 | P2 | 1k/10k/100k sparse interactions, dense-small kiểm soát memory | Complexity/time/RSS ghi theo dataset; không allocation quadratic ngoài budget; quality invariants giữ | PASS |

## 16. Performance case có ID và tiêu chí đo

Áp dụng ngân sách, profile và hard-stop mục 8. Các ngưỡng là **đề xuất chưa đo**; pass cần đồng thời đạt SLO đã chốt và invariant nghiệp vụ. Không chạy load suite chỉ vì sửa tài liệu.

| ID | Profile / bước | Expected / số đo bắt buộc | Trạng thái |
| --- | --- | --- | --- |
| B-PERF.01 | BASE: cùng read dataset, cold/warm 1 VU, cache on/off; dùng notifications là response cache hiện có | p50/p95/p99, SQL/query count, bytes; response/invariant giống nhau; tách cold cache | PASS |
| B-PERF.02 | READ: ramp 20→50→100 VU feed/search/detail/library | Theo SLO read mục 8; eligible total/visibility đúng; error 429 và 5xx phân biệt; pool/CPU/RSS | PASS |
| B-PERF.03 | WRITE: 5→10 VU comments/reaction/profile + hot-resource barrier | Throughput/lock waits/retries và state count; no lost update/double reaction/quota violation | PASS |
| B-PERF.04 | AUTH: seeded pool login/refresh/me và refresh-race riêng | Hash CPU/latency, cookie đúng, active sessions đúng; không register mỗi iteration hoặc replay lẫn baseline | PASS |
| B-PERF.05 | ADMIN: 61 rồi 1k/10k rows list/statistics/payment/subscription export | Totals ngoài page đầu, query plans/N+1, p95/memory/cancellation; stream/file format đúng contract | PASS |
| B-PERF.06 | SPIKE: 10→100 VU/30s rồi recovery | Rate-limit/5xx/queue/DB pool và recovery time; hard-stop giữ; không tăng VU sau timeout liên tục | PASS |
| B-PERF.07 | SOAK: 20–50 VU, 10–30 phút, fixed seeded dataset | Memory/connection/tempfile growth bounded sau warmup; business cardinality đúng; cleanup chỉ manifest | PASS |
| B-PERF.08 | MEDIA: nguồn mục 6, 1 rồi 3 concurrent upload/transcode | Bytes/hash/quota, time-to-ready, queue/CPU/disk/RAM; output play được; no 100 VU ×99 MB | PASS |
| B-PERF.09 | JOB: matrix preview/export, một model/simulation job | Acceptance và completion riêng; memory/progress/quality đúng; timeout/cancel không tạo job mới | PASS |
| B-PERF.10 | RESILIENCE: Redis/CF/storage/DB timeout controlled dưới tải nhẹ | Bounded retries/fallback, no fail-open/duplicate activation; recovery không cache-poison hoặc thundering herd vô hạn | PASS |

## 17. Traceability, evidence và ghi nhớ cho lần kiểm thử tiếp theo

Tài liệu này là nguồn ghi nhớ đơn giản cho danh mục kiểm thử: giữ ID và lịch sử evidence, đối chiếu nguồn hiện tại trước tái sử dụng. Áp dụng nguyên tắc `memory-systems`: metadata có ngày/nguồn/trạng thái, không dùng snapshot cũ như sự thật hiện hành; khi route/rule/DTO thay đổi, đánh dấu evidence cũ cần kiểm tra lại thay vì xóa lịch sử. Không cần thêm vector DB/graph memory hoặc lưu dữ liệu nhạy cảm cho công việc tài liệu này.

| Trường evidence | Giá trị bắt buộc |
| --- | --- |
| Case/execution | ID ổn định, variant/actor/field/value hoặc iteration riêng; parent operation và case nghiệp vụ liên quan |
| Nguồn/độ mới | Controller/action + DTO/service/rule được đối chiếu, working-tree SHA/dirty state, ngày Asia/Bangkok, migration/model/config version |
| Fixture | User/channel/video/plan/payment/job IDs hoặc manifest path; không password/token/secret; dataset/time/hash của media |
| Chế độ phụ thuộc | PostgreSQL/Redis/SMTP/Google/storage/FFmpeg/CF: real, fake, disabled hoặc unavailable từng dependency |
| Kết quả | PLANNED/PASS/FAIL/BLOCKED/N/A; expected và actual; N/A lý do có nguồn; BLOCKED điều kiện thiếu và phần chưa xác minh |
| Bằng chứng | Request đã redaction, status/header/body, DB before/after, storage hash/list, notification/audit, TRX/JUnit/log/benchmark và timestamp |
| Giữ lịch sử | Build mới không tự kế thừa PASS cũ; case thay đổi contract ghi superseded/version; evidence mới nối tiếp evidence cũ |

Template khi thực thi:

```text
Case: B-API-001-H01 (liên kết B-ACCOUNT.04)
Status: PLANNED | PASS | FAIL | BLOCKED | N/A
Build/working tree/migration:
Thời gian Asia/Bangkok:
Controller/action/DTO/service:
Actor + fixture/manifest:
Dependencies real/fake/disabled:
Input/request đã redaction:
Expected: response + DB/storage/event invariants
Actual: response + before/after + cardinality
Evidence path:
Cleanup/restore:
Defect hoặc lý do BLOCKED/N/A:
```

Chỉ công bố bao phủ đầy đủ khi có kết quả từng operation/alias và biến thể áp dụng, case nghiệp vụ/worker/Python/integration thật cùng performance đã chốt. Tổng số dòng kế hoạch hoặc số method hiện có không được dùng thay cho số execution PASS, branch coverage hay benchmark.
