# Bộ nhớ đệm Redis của Dalitu

Cập nhật ngày 07/11/2026. PostgreSQL vẫn là nguồn dữ liệu nghiệp vụ. Các cache phía Backend dùng Upstash Redis làm cache phân tán; API không giữ bản sao L1 trong RAM tiến trình. Khi Redis chưa cấu hình hoặc tạm thời không truy cập được, hệ thống đọc nguồn hoặc tính lại kết quả.

## Phạm vi

- `ReadModelCacheFilter` cache DTO thành công cho các API đọc nằm trong allowlist. Cache dùng khóa theo người dùng, revision và yêu cầu đã chuẩn hóa; kiểm tra xác thực và quyền vẫn chạy trước.
- Notification pages và snapshot counter đọc/ghi Redis trực tiếp qua HybridCache với local cache bị tắt.
- API quản trị cache DTO nhỏ cho matrix status/preview trong Redis tối đa 30 giây. Toàn bộ matrix đầu vào không được giữ trong cache; khi miss, dịch vụ dựng matrix từ PostgreSQL rồi tạo DTO.
- Recommendation Service cache danh sách video ID đã xếp hạng trong Upstash tối đa 30 giây, theo model version, user và danh sách loại trừ. Khi Redis lỗi hoặc chưa cấu hình, model tính lại; service không lưu bản sao ranking trong RAM.
- Tùy chọn âm lượng/tắt tiếng nằm trong hash phiên Redis đến khi phiên hết hạn. Khi Redis lỗi hoặc chưa cấu hình, API trả về mặc định và không giữ bản sao trong tiến trình.
- Kết quả GeoIP dùng key có IP đã băm, TTL 24 giờ. Nếu Redis lỗi, API gọi GeoIP provider và không giữ bản sao cục bộ.
- Thống kê usage của R2, Upstash và Neon được lưu trong Redis lần lượt 5, 5 và 15 phút. Khi Redis lỗi hoặc chưa cấu hình, API gọi provider lại.
- Cooldown khi Recommendation Service lỗi được chia sẻ qua Redis trong 30 giây. Marker lấy mẫu audit chữ ký webhook SePay dùng Redis `SET NX` trong 60 giây.
- Khóa Data Protection nằm trong Redis ở namespace riêng, không đặt TTL. Khóa mã hóa phải ổn định qua các lần triển khai.
- Web và Mobile vẫn có cache giao diện trong bộ nhớ trình duyệt/thiết bị. Đây là cache phía client, không phải tầng L1 của cache Redis trên Backend.

## Thời hạn lưu

| Nhóm | Thời hạn ở Redis | Ghi chú |
|---|---:|---|
| Danh mục công khai, lý do vi phạm, vai trò kênh | 30 phút | Revision catalog |
| Chính sách công khai | 30 giây | Quyết định policy hiệu lực vẫn đọc nguồn |
| Danh sách gói và nội dung hiển thị | 60 giây | Không cache my-plan, token mời hoặc checkout |
| Feed, video, kênh, playlist, Studio | 30 giây | Khóa riêng theo user khi có dữ liệu cá nhân |
| History, liked, download metadata | 15 giây | Phân vùng user/library |
| Danh sách quản trị | 15 giây | Quyền được kiểm tra trước cache |
| Hàng đợi kiểm duyệt, report, appeal, strike | 5 giây | Chỉ cache DTO hiển thị |
| Matrix status/preview DTO | 30 giây | Không cache toàn bộ matrix đầu vào |
| Lịch sử thanh toán | 5 giây | Trạng thái pending, webhook và quyền không cache chung |
| Trang thông báo | 30 giây | User, revision, page và pageSize |
| Snapshot counter quản trị | 15 giây | Snapshot số liệu PostgreSQL |
| Ranking video ID | 30 giây | Model version, user và danh sách loại trừ |
| Cooldown Recommendation Service | 30 giây | Chia sẻ trạng thái lỗi giữa các tiến trình API |
| GeoIP login | 24 giờ | Key dùng SHA-256 của địa chỉ IP |
| Usage R2 / Upstash / Neon | 5 / 5 / 15 phút | Chỉ cache số liệu từ provider |
| Lấy mẫu audit chữ ký SePay lỗi | 60 giây | Redis `SET NX`; không lưu body webhook |
| Tùy chọn phát theo phiên | Đến lúc phiên hết hạn | Hash Redis; không có bản sao trong RAM API |
| Data Protection key ring | Không đặt TTL | Namespace riêng, giá trị được mã hóa |

DTO của Backend có jitter nhỏ dưới 10%. `ReadModelCacheFilter` không lưu lỗi HTTP, response rỗng, file hoặc payload lớn hơn 256 KiB. Endpoint ngoài allowlist, endpoint ghi và endpoint cần trạng thái tức thời tiếp tục đọc nguồn.

## Khóa Redis

Cache DTO Backend dùng prefix `dalitu:<environment>:cache:`. Python Recommendation Service dùng cùng prefix; nội dung user ID và danh sách loại trừ được băm trước khi làm thành phần khóa.

| Key logic | Giá trị |
|---|---|
| `read:v1:<public-or-user>:<revisionHash>:<requestHash>` | JSON DTO thành công |
| `notifications:v1:<user>:<revision>:<page>:<pageSize>` | Trang inbox, tổng số và unread |
| `operations:counters:v1:<15-second-bucket>` | Snapshot counter DB |
| `recommendation:rank:v1:<sha256>` | Danh sách video ID theo model/user/exclusions |
| `recommendation-unavailable:<serviceUrl>` | Cooldown gọi Recommendation Service |
| `admin-r2-storage-usage:<account>:<bucket>` | Số liệu usage R2 |
| `admin-neon-usage:<org>:<project>` | Số liệu usage Neon |
| `admin-upstash-usage:<database>` | Số liệu usage Upstash |
| `auth:geoip:v1:<sha256(ip)>` | Vị trí suy ra từ IP; không lưu IP thô trong key |
| `payments:sepay-invalid-signature:v1` | Marker lấy mẫu audit chữ ký sai |
| `dalitu:auth:session:<sessionId>` | Hash gồm `volume`, `muted`, hết hạn cùng phiên |

Các thành phần trong dấu ngoặc là mẫu. Không đưa credential hoặc access token vào cache key. Data Protection dùng key `dalitu:<environment>:data-protection:keys`, tách khỏi các key có TTL.

## Vô hiệu hóa khi dữ liệu đổi

Migration `20261024080000_ReadCacheRevisions` tạo bảng PostgreSQL `read_cache_revisions` với `scope` và `revision`. Trigger xoay revision trong cùng transaction nghiệp vụ. Khi request đọc bắt đầu, API lấy các revision liên quan từ PostgreSQL, tạo cache key rồi mới đọc Redis. Nếu dữ liệu đổi, key mới không khớp cache entry cũ; entry cũ tự hết TTL.

Vì revision được đọc trước mỗi lookup, một cache hit vẫn có truy vấn nhỏ tới PostgreSQL. Redis giữ DTO; PostgreSQL giữ dữ liệu nghiệp vụ và trạng thái revision. Nếu Redis lỗi, DTO được dựng từ nguồn, notification đọc PostgreSQL, ranking được tính lại từ model, GeoIP gọi provider và usage được truy vấn lại. API không dùng cache dữ liệu trong RAM làm phương án thay thế.

## Cấu hình và triển khai

API dùng `UPSTASH_REDIS_REST_URL` và `UPSTASH_REDIS_REST_TOKEN` hoặc cấu hình tương đương `Redis__Endpoint`/`Redis__Password`. Adapter API gửi HTTPS POST dạng JSON, timeout 3 giây, và tạm ngừng gọi Redis 30 giây sau khi phát hiện lỗi. Có thể đặt `Redis__Enabled=false` để tắt tích hợp; `ReadCache__Enabled=false` chỉ bypass DTO cache.

Recommendation Service cũng cần `UPSTASH_REDIS_REST_URL` và `UPSTASH_REDIS_REST_TOKEN` trong môi trường chạy. Với Docker Compose EC2, thêm hai biến này vào `recommendation-service/.env.ec2`; chỉ cấp URL và REST token Redis, không truyền toàn bộ file secret của API sang container này. Nếu thiếu cấu hình, ranking vẫn chạy nhưng không cache. Request Redis dùng timeout 1 giây và lỗi cache không làm hỏng response đề xuất.

Các endpoint Redis không được ghi credential vào repo. Khi deploy, `migrate` phải chạy thành công trước API. Xem thêm [CI/CD](CI_CD.md), [EC2 auto deploy](EC2_AUTO_DEPLOY.md) và [cấu hình Recommendation](RECOMMENDATIONS.md).

## Đánh giá

Header `X-Data-Cache` cho biết hit/miss của DTO cache chung, không chứng minh request không đọc PostgreSQL vì bước lấy revision vẫn chạy. Cần đo latency, số lệnh Redis, hit/miss, dung lượng Upstash và lượng RAM tiến trình khi tải thực tế. Chưa có benchmark tải đủ để kết luận mức tăng tốc hay sức chứa người dùng.

Tham khảo: [Microsoft HybridCache](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0), [PostgreSQL CREATE TRIGGER](https://www.postgresql.org/docs/17/sql-createtrigger.html).
