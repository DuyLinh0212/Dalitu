# Vận hành thuật toán đề xuất

## Luồng dữ liệu

Super Admin mở `/recommendations` trên Admin Web. Browser chỉ gọi `/api/v1/admin/recommendations/*` của API .NET. `Kiểm tra dữ liệu` tạo ma trận xác định từ lịch sử xem, reaction, rating và bình luận của tài khoản đang hoạt động; đăng ký kênh tăng tín hiệu cho những cặp user–video đã có hành vi video. CSV chỉ gồm ID và tín hiệu số/boolean, không gồm email, mật khẩu hay nội dung bình luận. Diff so sánh từng cặp với CSV mà manifest R2 `collaborative_cf/active.json` trỏ tới. Trang có bảng xem trước 20 cặp và nút xuất toàn bộ CSV.

Score huấn luyện luôn nằm trong `[0,1]`: rating dùng `(rating - 1) / 4`, watch ratio giữ nguyên sau khi kẹp, like/comment/subscribe là nhị phân; dislike là tín hiệu phạt với giá trị ưu tiên bằng `0`. Chỉ các tín hiệu thực sự xuất hiện trên cặp user–video mới tham gia mẫu số, để dữ liệu thiếu không bị hiểu nhầm là hành vi tiêu cực. Chế độ mặc định là `average` với trọng số bằng nhau; chế độ `weighted` nhận trọng số do Super Admin nhập trên giao diện. Cấu hình được lưu trong payload job, `metadata.json` của artifact và `active.json` trên R2.

`Cập nhật mô hình` trả `202` với job ID trong PostgreSQL. Worker kiểm tra tối thiểu 2 user, 2 video, 3 cặp, xuất CSV vào `collaborative_cf/YYYY/MM/DD/interactions_<UTC timestamp>_<12 ký tự SHA-256>.csv`, rồi gọi Recommendation Service bằng admin token kèm cấu hình `scoreAggregation`. Service xác minh SHA-256, huấn luyện Item-Based Cosine trên score chuẩn hóa, lưu ZIP artifact trên R2, nạp kiểm tra và cuối cùng ghi `active.json`. Sau khi manifest mới được xác nhận, API dọn các CSV ma trận cũ trong prefix `collaborative_cf/` và giữ lại CSV đang hoạt động; artifact ZIP và `active.json` không bị xóa. Nếu bất kỳ bước nào thất bại, manifest và model trong bộ nhớ cũ không đổi. Job ghi bước lỗi và log để admin theo dõi. Service khởi động lại đọc manifest và artifact từ R2, xác minh hash rồi mới phục vụ.

Endpoint suy luận dùng token khác. Backend gửi toàn bộ ID video đã xem mới nhất trong `excludeItemIds`, loại kết quả CF đã xem, và loại video đã xem khỏi tập phổ biến dùng để bù trên mọi trang. Video đã xem vẫn nằm trong ma trận tính tương đồng. Tìm kiếm video thường không dùng bộ lọc này.

## Cấu hình local

Web/mobile lấy feed trực tiếp từ FastAPI `GET /api/v1/feed/recommended` với bearer token. Cấu hình client bằng `RECOMMENDATION_BASE_URL` (web runtime config hoặc Flutter `--dart-define`). Python dùng `HUTUBE_API_BASE_URL` để lấy context/dựng video cards từ .NET, và `CORS_ALLOWED_ORIGINS` (mảng JSON) chứa origin User Web. Refresh token/cookie chỉ gửi về .NET. Chi tiết luồng và các giá trị local/Render nằm trong [README dịch vụ](../../recommendation-service/README.md).

1. Tạo `recommendation-service/.env` theo `.env.example` với `RECOMMENDER_SERVICE_TOKEN`, `RECOMMENDER_ADMIN_TOKEN` **khác nhau**, cùng `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET_NAME`. Không commit file `.env`.
2. Cấu hình API .NET qua biến môi trường `Recommendation__ServiceUrl=http://127.0.0.1:8000`, `Recommendation__ServiceToken`, `Recommendation__AdminToken` tương ứng, cùng `Storage__R2__AccountId`, `Storage__R2__AccessKeyId`, `Storage__R2__SecretAccessKey`, `Storage__R2__BucketName`.
3. Chạy migration theo hướng dẫn backend trước khi khởi động API. Trong `recommendation-service`, cài `py -3 -m venv .venv` và `.\.venv\Scripts\python.exe -m pip install -e ".[dev]"`; từ gốc repo chạy `./scripts/run-recommender.ps1` (bind `127.0.0.1`).
4. Đăng nhập Super Admin, mở `/recommendations`, chọn `Kiểm tra dữ liệu`, chạy mô phỏng nếu cần, rồi chọn `Cập nhật mô hình`. Các tài khoản thật cần xác nhận trên giao diện; backend vẫn kiểm tra xác nhận khi gọi API trực tiếp.

Trong phần mô phỏng, có thể chọn nhiều danh mục và chia tỷ lệ tổng 100% cho từng danh mục. Kịch bản **User mục tiêu** cho từng user tương tác với các video đã chọn; kịch bản **Cụm bot / user** chọn video theo trọng số danh mục. Tỷ lệ xem là xác suất tạo lượt xem, còn thời lượng xem là phần trăm thời gian video được ghi nhận. **Tỷ lệ bỏ qua video** được gieo ngẫu nhiên độc lập cho từng lượt; video bị bỏ qua chỉ xuất hiện trong log/audit và không tạo bất kỳ tín hiệu view, reaction, rating, bình luận hoặc đăng ký nào. Backend bỏ qua reaction, rating và đăng ký đã tồn tại, không cho user thích/đánh giá video hoặc đăng ký kênh của chính mình; dislike không tạo rating số và không mở thêm lượt đăng ký.

Chi tiết nguyên lý, payload và từng bước xử lý nằm trong [BOT_SIMULATION.md](BOT_SIMULATION.md).

## Cache ranking trên Upstash

Recommendation Service có thể dùng cùng Upstash Redis làm cache phân tán cho danh sách video ID đã xếp hạng. Key gồm model version, user và danh sách loại trừ đã chuẩn hóa; phần định danh được băm trước khi lưu. TTL là 30 giây. Service không giữ bản sao ranking trong RAM.

Đặt `UPSTASH_REDIS_REST_URL` và `UPSTASH_REDIS_REST_TOKEN` trong môi trường Recommendation Service. Local launcher nhận các biến đã nạp vào process hoặc từ `recommendation-service/.env`; Docker EC2 nạp chúng từ `recommendation-service/.env.ec2`; Render nhận chúng trong Environment của service Python. Nếu thiếu cấu hình hoặc Redis lỗi, cache được bỏ qua và model tính ranking lại.

## Render Free

`render.yaml` khai báo `hutube-recommendation-service` dạng public Docker web miễn phí. Điền cùng cặp token và R2 credentials cho hai service trong Render; đặt `Recommendation__ServiceUrl` trên API thành URL HTTPS của service Python. Giữ token trong biến môi trường, không trong web config. API và Python phải dùng chung bucket R2. R2 API token của API cần quyền đọc/ghi/xóa object và liệt kê object trong bucket để sweep CSV cũ. [Render Free](https://render.com/docs/free) có 512 MB RAM, 0,1 CPU, ngủ sau 15 phút không có request và file cục bộ không bền; vì vậy artifact chỉ được lưu trên R2. Request đầu sau khi service ngủ có thể dùng feed mặc định. [R2 S3 API](https://developers.cloudflare.com/r2/get-started/s3/) cần endpoint account và region `auto`.

Để kiểm tra triển khai, xác nhận `GET /health` của Python trả model version, CSV key, hash và thời điểm cập nhật sau khi job hoàn thành; sau khi restart service, `GET /ready` vẫn trả cùng version. Kiểm tra job thất bại không thay `active.json`. Thử một user đã xem video trước và sau lúc huấn luyện trên trang 1 và trang tiếp theo.

## Dữ liệu cũ

Simulator cũ và CSV xuất ở gốc repository đã bị xóa khỏi trạng thái cuối. Branch nguồn từng chứa 5 tài khoản `.invalid` cùng mật khẩu trong `registered_users.json`. Không dùng lại những mật khẩu đó. Nếu ai đã dùng cùng mật khẩu cho tài khoản thật hoặc dịch vụ khác, người sở hữu cần đổi ngay; squash merge chỉ ngăn dữ liệu đó đi vào lịch sử `develop`, không xóa commit đã tồn tại trên branch nguồn.
