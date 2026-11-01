# Danh mục phạm vi kiểm thử

`generate_inventory.py --openapi <file>` đối chiếu source route/event bindings (kể cả inline template), OpenAPI runtime và tên test hiện có. JSON chỉ là catalog kế hoạch; không khẳng định assertion/branch coverage. Paths là tương đối repository, có dòng source cho bindings.

`web-inventory.json`: route declarations (có alias/redirect/conditional), event bindings, href/routerLink navigation, input/textarea/select và nhóm Web; bản `upload copy` không routed được ghi excluded. `backend-endpoints.json`: từng method/path cùng nhóm Backend, biến thể cần test và đường dẫn ngoài OpenAPI. `existing-tests.json`: tên method Fact/Theory/InlineData và Python.

`verify_catalog.py` kiểm tra các nhóm tham chiếu có trong tài liệu, method có trong phụ lục, links tồn tại, report và cấu trúc ảnh hợp lệ. Đây là kiểm tra tính nhất quán của catalog/bằng chứng, không phải kiểm thử chức năng toàn bộ Web. `organize_screenshots.py --apply` sắp ảnh theo ngày chạy/nhóm/phạm vi/test case, cập nhật đường dẫn đã ghi nhận và giữ nguyên bytes. `publish_auth_evidence.py` tổng hợp ảnh thật và bảng run local; không copy trace/mail/state.

Sau thay đổi source phải regenerate và reconcile với `docs/WEB_TEST_SCENARIOS.md`, `docs/BACKEND_TEST_SCENARIOS.md`. Mapping nhóm không thay thế case/assertion từng hành động. Rà thủ công href/routerLink/guard/feature flags/dynamic branching và service side effects mà regex chưa biểu diễn. Không tự chuyển PLANNED sang PASS.

Web suite: `verify_web_evidence.py` kiểm tra report User/Admin, counts, PNG cùng run
và ảnh PASS đầu tiên được giữ khi fixture reuse. `build_web_progress.py` tạo
`docs/WEB_E2E_PROGRESS.md` và `scope-progress.json`, tách case đã chạy khỏi inventory
còn phải reconcile. CF Seeder/simulation được loại khỏi phạm vi theo yêu cầu người dùng.
`export_web_report.mjs` author workbook local bằng artifact-tool;
`export_ci_report.py` là fallback openpyxl cho GitHub Actions.
