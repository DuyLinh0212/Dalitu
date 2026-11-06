# Kiểm thử mobile

Chạy từ `mobile/user-app`:

```sh
flutter analyze
flutter test
```

Giữ test theo hành vi và lỗi hồi quy, không theo từng lần chỉnh giao diện:

- `auth_test.dart`, `auth_registration_navigation_test.dart`, `flows_test.dart`, `widget_test.dart`: đăng nhập, phiên, đăng ký, điều hướng và khôi phục tài khoản.
- `profile_links_parity_test.dart`, `channel_loading_regression_test.dart`, `channel_upload_test.dart`, `web_user_channel_parity_test.dart`: hồ sơ/liên kết, quyền quản lý kênh, kênh tạm ngưng, ảnh kênh và lỗi tải từng phần.
- `background_runtime_test.dart`, `picture_in_picture_bridge_test.dart`, `media_upload_refresh_test.dart`: quyền gói dịch vụ, phát nền, PiP và cài đặt media.
- `download_runtime_test.dart`, `download_security_test.dart`, `download_http_integration_test.dart`, `offline_download_test.dart`, `downloads_redesign_test.dart`: yêu cầu tải từ trang xem, chính sách mạng, URL/quyền truy cập, tải file thật, manifest/ảnh và xem offline.
- `video_upload_transport_test.dart`, `upload_result_test.dart`, `mobile_feature_parity_test.dart`: chunk upload, retry/hủy, phân biệt phiên upload với trạng thái video đã lưu/chờ duyệt, metadata và bản nháp.
- `watch_interaction_test.dart`, `autoplay_next_test.dart`, `comment_interaction_ui_test.dart`, `web_user_comment_parity_test.dart`, `watch_feedback_style_test.dart`: một native player, điều hướng mini-player, autoplay, phản ứng, bình luận/trả lời/phân trang và khôi phục khi API lỗi.
- `creation_actions_test.dart`: tạo danh sách phát/video và lưu thứ tự playlist.
- `main_sync_regression_test.dart`, `mobile_performance_test.dart`, `source_audit_regression_test.dart`: tải độc lập, giới hạn thời gian, cache, race và response đến trễ. `source_audit_regression_test.dart` có test hành vi thực, không chỉ kiểm tra mã nguồn.
- `search_team_evidence_parity_test.dart`, `studio_policy_evidence_test.dart`: tìm kiếm, thành viên kênh, kháng nghị/minh chứng và đồng bộ chính sách.
- `mobile_screen_audit_test.dart`, `mobile_ui_refresh_test.dart`, `mobile_popup_regression_test.dart`, `settings_functional_test.dart`: bố cục, chữ lớn, bàn phím và lưu/khôi phục cài đặt. Các test UI chức năng còn lại tiếp tục chạy cùng toàn bộ suite.

Các file nhỏ liên quan đã được gộp vào các suite trên; giữ nguyên các ca kiểm thử chức năng. Hai suite xuất ảnh/kiểm tra bố cục trùng phạm vi (`parity_visual_audit_test.dart`, `studio_refinement_visual_test.dart`) được bỏ. Các suite còn lại kiểm tra cả lỗi hiển thị và hành vi thực tế.

`fixtures/` chứa video/ảnh nhỏ phục vụ kiểm tra file thật; không xóa chỉ vì là tệp nhị phân. `auth_widget_helpers.dart` cung cấp helper dùng chung, không phải test thừa.

`api_smoke_test.dart` được bỏ qua khi chưa có cấu hình môi trường test. Chỉ bật trên môi trường thử riêng; không cung cấp tài khoản hoặc bí mật production để chạy suite thông thường. PiP hệ thống, picker và quyền Android vẫn cần kiểm thử thêm trên thiết bị.

Lượt kiểm tra cuối sau tối ưu hiệu năng và bố cục upload: 385 test đạt, 1 live API smoke test bỏ qua do thiếu cấu hình; analyzer sạch. Test hiệu năng kiểm tra player không rebuild theo từng nhịp tiến độ, đồng thời tiến độ hiển thị vẫn cập nhật đúng.
