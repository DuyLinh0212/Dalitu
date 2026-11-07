# Tài liệu Dalitu

Trang này là bản đồ tài liệu trong repository. Tổng quan sản phẩm và cách chạy nhanh nằm ở [README gốc](../README.md).

## Tổng quan

- [Tổng quan sản phẩm](PRODUCT.md)
- [Stack công nghệ](architecture/TECH_STACK.md)
- [Kiến trúc hệ thống](architecture/PROJECT_ARCHITECTURE.md)
- [Kiến trúc Collaborative Filtering](architecture/CF_ARCHITECTURE.md)
- [Quy ước phát triển](conventions/DEVELOPMENT_CONVENTIONS.md)

## Tính năng và thiết kế

- [Module người dùng](features/Module_NguoiDung.md)
- [Module quản trị](features/Module_QuanTri.md)
- [Module Collaborative Filtering](features/Module_CF.md)
- [Chính sách và kiểm duyệt](features/HuTube_Policy_Moderation.md)
- [Moderation và RabbitMQ](features/Moderation_RabbitMQ.md)
- [Design system](design/design.md)

## API và dữ liệu

- [Auth/session API](api/S4_AUTH.md) · [RBAC API](api/S4_RBAC.md)
- [Quy trình thay đổi database](database/DATABASE_CHANGE_MANAGEMENT.md) · [Ràng buộc dữ liệu](database/HuTube_RangBuoc.md)
- [Quyết định PostgreSQL local](decisions/ADR-008-postgresql-and-local-s4.md)

## Kiểm thử

- [Kịch bản backend](BACKEND_TEST_SCENARIOS.md) · [Kịch bản Web](WEB_TEST_SCENARIOS.md)
- [Ma trận RBAC](testing/Admin_RBAC_Matrix.md)
- [Tiến độ E2E Web](WEB_E2E_PROGRESS.md) · [Báo cáo hiệu năng Admin](testing/Admin_Performance_Review_20261025.md)
- [Tài liệu lỗi và xử lý](BUG.md) · [Tiến độ xử lý lỗi](bug-fix-progress.md)

## Vận hành

- [Chạy local](operations/LOCAL_DEVELOPMENT.md) · [PostgreSQL local](operations/LOCAL_SETUP.md)
- [Cache Redis L2](operations/REDIS_READ_CACHE.md) · [Player preferences](operations/SESSION_PLAYER_PREFERENCES.md)
- [Recommendation](operations/RECOMMENDATIONS.md) · [Thống kê model](operations/MODEL_STATISTICS.md)
- [Tải và xử lý video](operations/VIDEO_UPLOADS.md) · [Giám sát hệ thống](operations/SYSTEM_MONITORING.md)
- [CI/CD](operations/CI_CD.md) · [Tự động deploy EC2](operations/EC2_AUTO_DEPLOY.md)
- [SePay](operations/SEPAY.md) · [Mô phỏng bot](operations/BOT_SIMULATION.md)

## Quy ước đọc

- `features/` và `architecture/` mô tả nghiệp vụ và thiết kế. README gốc cùng mã nguồn là điểm tham chiếu cho cách chạy hiện tại.
- Scenario là danh mục cần kiểm tra; trạng thái PASS chỉ có giá trị trong phạm vi, ngày chạy và bằng chứng được ghi trong tài liệu.
- Báo cáo có ngày là ảnh chụp của lần kiểm tra đó, không tự động đại diện cho trạng thái hiện tại của code.
