# HuTube Mobile — S4-01 / S4-02

Ứng dụng Flutter dùng chung API với User Web và Admin Web. APK build trên PC mặc định dùng API `https://api.dalitu.app/api/v1`, recommendation `https://recommendation.dalitu.app/api/v1` và origin xác minh email `https://dalitu.app`. CI/CD tiếp tục xuất APK artifact với các URL môi trường tương ứng; không build APK trên EC2. Chạy USB/emulator local cần ghi đè bằng `--dart-define` hoặc dùng `scripts/run-local.ps1 -Component mobile`.

## Chạy local

Backend cần chạy cổng 5080. Theo quyết định hiện tại của chủ dự án, local thay Staging cho giai đoạn này.

Feed cá nhân hóa gọi trực tiếp FastAPI tại `RECOMMENDATION_BASE_URL`, dùng access token đăng nhập và tự refresh qua .NET khi hết hạn. Với Android Emulator, URL tự suy ra là `http://10.0.2.2:8000/api/v1`; với API local trên localhost/127.0.0.1, dùng cùng host ở cổng 8000. Bản deploy hiện dùng API `https://api.dalitu.app/api/v1` và Recommendation Service `https://recommendation.dalitu.app/api/v1`. Ghi đè bằng `--dart-define=RECOMMENDATION_BASE_URL=...` cho URL khác, đặc biệt khi dùng IP LAN. Với Android USB, thêm `adb reverse tcp:8000 tcp:8000`; script `run-local.ps1 -Component mobile` tự thiết lập cả hai cổng.

```powershell
flutter pub get
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:5080/api/v1 --dart-define=AUTH_WEB_BASE_URL=http://localhost:4200
```

- Android Emulator: truyền `API_BASE_URL=http://10.0.2.2:5080/api/v1` để kết nối máy host.
- Điện thoại Android nối USB: chạy `adb reverse tcp:5080 tcp:5080` rồi dùng `http://127.0.0.1:5080/api/v1`.
- iOS Simulator: dùng `http://localhost:5080/api/v1` khi backend chạy trên máy Mac. iOS cần Xcode để build.
- Điện thoại thật cùng LAN: truyền IP LAN máy backend và bind backend vào địa chỉ có thể truy cập. Không lưu URL vào source để chuyển môi trường.
- Build APK trên PC: `flutter build apk --release`. Có thể ghi đè `API_BASE_URL`, `RECOMMENDATION_BASE_URL` và `AUTH_WEB_BASE_URL` khi đổi môi trường; Android chỉ cho HTTP ở debug/profile.

Biểu tượng kết nối trên thanh đầu trang gọi `GET /system/info`, hiện trạng thái thực từ backend.

## Google Sign-In Android

Mobile không dùng một client ID được ghi cứng trong mã nguồn. Khi chạy qua
`./scripts/run-local.ps1 -Component mobile`, script chuyển `Google__ClientId`
từ `.env.local` thành `GOOGLE_WEB_CLIENT_ID`; khi chạy/build trực tiếp, truyền
cùng Web OAuth client ID đang cấu hình ở backend:

```powershell
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:5080/api/v1 --dart-define=GOOGLE_WEB_CLIENT_ID='<web-oauth-client-id>'
```

Trong Google Cloud Console phải có OAuth client **Android** với package name
`com.hutube.user_app` và SHA-1/SHA-256 của đúng khóa ký APK. Lấy fingerprint
debug bằng `cd android; ./gradlew signingReport`; trước release, thêm
fingerprint của upload/release key nữa. Backend `Google__ClientId` phải là
chính Web OAuth client ID truyền vào mobile. Không commit `google-services.json`, private key hoặc client secret.

## Auth và phiên

Đăng ký → xác minh email → đăng nhập. Hỗ trợ gửi lại xác minh, quên/đặt lại mật khẩu, khôi phục phiên, tự refresh một lần khi API trả 401, tải/thu hồi phiên và đăng xuất thiết bị khác. Tài khoản bị tạm khóa/cấm được xử lý từ lỗi backend.

Access token chỉ ở bộ nhớ. Refresh token nằm trong `flutter_secure_storage` (Keychain / Android encrypted storage); không ghi token, mật khẩu vào log hoặc UI. Refresh đồng thời được hợp nhất. Phản hồi đăng nhập/refresh đến sau logout bị bỏ và phiên mới bị thu hồi. Mất mạng khi khôi phục không xóa refresh token để người dùng có thể thử lại. Logout xóa phiên thiết bị ngay; nếu chưa thu hồi được trên server, thông báo nêu rõ trạng thái đó.

## Deep link

Đã đăng ký scheme `hutube` trên Android và iOS. App chỉ nhận host `auth` và các route được whitelist:

- `hutube://auth/account`: yêu cầu đăng nhập rồi mở tài khoản.
- `hutube://auth/login`
- `hutube://auth/verify-email?token=<url-encoded-token>`

Email xác minh có thể mở app bằng liên kết trên. Đặt lại mật khẩu không dùng deep link: nhập email để nhận OTP, sau đó nhập mã trong ứng dụng. Sau đăng ký, màn kiểm tra hộp thư cho phép gửi lại email hoặc dán liên kết xác minh. Mobile chỉ nhận liên kết xác minh đúng origin `AUTH_WEB_BASE_URL` (mặc định `https://dalitu.app`) hoặc scheme đã khai báo, trích token rồi gửi tới API của app. Truyền `--dart-define=AUTH_WEB_BASE_URL=...` khớp `Auth__WebBaseUrl` của backend khi đổi môi trường, ví dụ `http://localhost:4200` cho local. CI dùng biến `MOBILE_AUTH_WEB_BASE_URL` trong environment `mobile`. Không có API giả trả mã. Chế độ gửi email phụ thuộc cấu hình backend (SMTP hoặc pickup). Trên Android có thể kiểm tra routing bằng `adb shell am start -a android.intent.action.VIEW -d "hutube://auth/account" com.hutube.user_app`.

Custom scheme phục vụ local. Trước khi phát hành, cấu hình HTTPS App Links / Universal Links với domain đã sở hữu để xác minh chủ ứng dụng.

## Phát nền và tải xuống

Khi người dùng chọn một video mới, app tự phát sau khi tải nguồn thành công,
không cần bấm Play. Lựa chọn tự phát video tiếp theo chỉ điều khiển chuyển
video khi kết thúc; không chặn phát video được chọn thủ công. Mở lại video
thông thường giữ tiến độ đã lưu; luồng chuyển tiếp bắt đầu video từ đầu.

Cài đặt Wi-Fi only áp dụng cho tải thủ công; tải thông minh luôn yêu cầu Wi-Fi, tài khoản đủ quyền, tối đa 3 video và hạn mức dung lượng đã chọn. Chất lượng mặc định áp dụng khi tạo yêu cầu tải; Hỏi mỗi lần mở bộ chọn, còn tải thông minh dùng tối đa 480p cho lựa chọn này. Nếu không có chất lượng phù hợp, tải thủ công hỏi lại và tải thông minh bỏ qua. Đề xuất tải có thể ẩn độc lập.

Phát nền kiểm tra quyền gói và chế độ Tắt / Chỉ tai nghe hoặc loa ngoài / Luôn bật; tháo thiết bị âm thanh ở nền sẽ dừng nếu chế độ yêu cầu. Tải file vẫn chạy trong tiến trình app, chưa bảo đảm tiếp tục khi hệ điều hành dừng tiến trình. Các ca chưa xác minh trên thiết bị được ghi trong [checklist](MANUAL_DEVICE_TEST_CHECKLIST.md).

PiP thủ công yêu cầu quyền gói; bật cài đặt PiP chỉ quyết định tự mở khi ra
Home, không cấp thêm quyền. Android dùng Activity và player đang có để vào
PiP, giữ nguyên tiến độ. Video Android render bằng Flutter texture; PiP lấy
đúng vùng video sau khi ẩn giao diện, không giữ khung nền của player thu nhỏ.
Khi video đang phát và cài đặt PiP bật, Android 12+ được đăng ký tự vào PiP
ngay từ lúc xem; Android cũ hơn mở PiP trực tiếp khi về Home. Luồng tự động
không đợi Flutter dựng thêm khung hình sau khi app đã chuyển nền. Tạm dừng,
đóng video hoặc tắt cài đặt sẽ tắt đăng ký tự động.
Phát nền ở chế độ tai nghe có trạng thái Chờ tai nghe
và có thể tắt yêu cầu ngay từ player. Các ca PiP/khóa màn hình trên APK mới
vẫn cần xác nhận bằng thiết bị theo checklist.

Upload hoàn tất khi API trả `videoId`, kể cả khi video còn mang trạng thái
`processing` để tạo chất lượng hoặc chờ kiểm duyệt. Khay upload thông báo đã
lưu và trạng thái tiếp theo; chỉ báo đã xuất bản khi server trả `published`.

## Hiệu năng player và thumbnail

Màn xem và mini-player giữ nguyên widget video khi chỉ cập nhật tiến độ;
khi đổi controller, widget vẫn được cập nhật theo controller mới. Test 40 nhịp
tiến độ, mỗi nhịp 250 ms, giảm số rebuild widget native từ 40 xuống 0 ở cả
hai chế độ; thanh tiến độ vẫn cập nhật. Đây là số đo widget test, chưa phải
số đo FPS hoặc pin trên điện thoại thật.

Thumbnail Khám phá giới hạn kích thước giải mã theo chiều rộng hiển thị và
mật độ điểm ảnh thiết bị. Bố cục, URL ảnh và luồng thao tác giữ nguyên.

## Kiểm tra

CI dùng Flutter **3.47.2 stable** (xem `.github/workflows/ci.yml` tại repo root).
Nên chạy kiểm tra local cùng phiên bản: SDK cũ có thể chưa có các assertion
Material/ListTile đang được CI kiểm tra. Các surface chứa hàng nhấn dùng
`Material` để nền và hiệu ứng nhấn được vẽ đúng.

```powershell
flutter --version
flutter analyze
flutter test --reporter expanded
flutter build apk --debug --dart-define=API_BASE_URL=http://10.0.2.2:5080/api/v1
```

Test controller kiểm tra rotation, 401 đồng thời, phiên hết hạn, mất mạng, phản hồi trễ sau logout, suspended, whitelist link và password. Widget test kiểm tra validation, deep link qua login, lỗi suspended, link reset thiếu token và bố cục khi bàn phím mở trên màn hình hẹp. HTTP trong test được thay bằng mock có assertion contract; runtime luôn dùng API thật.

APK debug phục vụ local; cấu hình ký release của scaffold chưa phải khóa phát hành và không được dùng để đưa lên store.

### Smoke với API và PostgreSQL thật

```powershell
flutter test test/api_smoke_test.dart --dart-define=LIVE_API_BASE_URL=http://localhost:5080/api/v1 --dart-define=EMAIL_PICKUP_DIRECTORY=F:/NgDuyLinh/Khoa_Luan_Tot_Nghiep/HuTube/.work/mail --reporter expanded
```

Chỉ chạy với Development pickup email. Test tạo một tài khoản smoke có email duy nhất, kiểm tra Register → Verify → Login → Me → Refresh → Restore → Logout others → Forgot/Reset → từ chối phiên cũ → Login mới → Logout. Mặc định test được bỏ qua khi thiếu hai biến, nên unit/widget suite không đòi backend. HTTP và AuthController trong smoke là code thật; TokenStore thay bằng memory để chạy trên Windows, không thay cho kiểm thử Keychain/Android trên thiết bị.

Dòng `flutter_secure_storage` 10 được khóa theo major vì tương thích compileSdk 36 của Flutter hiện tại; dòng 11 yêu cầu SDK 37. Khóa resolved dependencies trong `pubspec.lock` được commit để CI dùng cùng phiên bản.

Báo cáo kiểm thử hiện tại: [FUNCTIONAL_TEST_REPORT.md](FUNCTIONAL_TEST_REPORT.md).
Checklist tự kiểm trên máy thật: [MANUAL_DEVICE_TEST_CHECKLIST.md](MANUAL_DEVICE_TEST_CHECKLIST.md).
