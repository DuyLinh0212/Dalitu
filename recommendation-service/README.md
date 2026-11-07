# HuTube Recommendation Service

Service FastAPI cho hai biến thể Collaborative Filtering thuần:

- User-Based CF: tìm user có lịch sử tương tác tương tự.
- Item-Based CF: tìm video tương tự với các video user đã tương tác.

Pipeline hiện tại tập trung vào việc xây dựng ma trận tương đồng, tính điểm Top-K
và đo chi phí chạy. Không sinh chỉ số offline để kết luận video có phù hợp hoặc
được user yêu thích hay không, vì dữ liệu benchmark chưa có nhãn phù hợp độc lập.

## Pipeline

```text
MovieLens / UnifiedInteraction
          |
          v
Ma trận user-item X
          |
          +--> similarity giữa user-user  --> User-Based CF
          |
          +--> similarity giữa item-item  --> Item-Based CF
                                      |
                                      v
                             score item chưa xem
                                      |
                                      v
                                  Top-K
                                      |
                                      v
                       runtime + complexity report
```

`train` là bước rebuild offline: tính similarity, lưu artifact và ghi lại thời
gian fit của từng biến thể. Request FastAPI chỉ load artifact và ranking.

Feed trực tiếp lấy context đã xác thực từ API .NET, sau đó Recommendation Service
xếp hạng ID video. Kết quả ranking có thể dùng Upstash Redis làm L2 trong 30 giây,
được phân theo model version, user và danh sách video loại trừ. Service không giữ
bản sao ranking trong RAM; nếu Redis lỗi hoặc chưa cấu hình, model tính lại. Đặt
`UPSTASH_REDIS_REST_URL` và `UPSTASH_REDIS_REST_TOKEN` trong môi trường của Python
service để bật cache này.

## Công thức chính

### Cosine similarity

Với hai vector tương tác `a` và `b`:

```text
sim_cos(a,b) = (sum_t a_t*b_t)
               / (sqrt(sum_t a_t^2) * sqrt(sum_t b_t^2))
```

Item-Based dùng `X.T` để mỗi item trở thành một vector theo user:

```text
S_item = similarity(X.T, X.T)
```

User-Based dùng trực tiếp các hàng của `X`:

```text
S_user = similarity(X, X)
```

### Jaccard và Pearson

```text
sim_jaccard(A,B) = |A ∩ B| / |A ∪ B|
```

Jaccard chỉ nhìn việc có hoặc không có tương tác. Pearson đo tương quan giữa các
rating cùng được quan sát sau khi trừ trung bình rating. Sáu biến thể được train
là ba similarity cho mỗi family User-Based và Item-Based.

### Điểm xếp hạng User-Based

Gọi `N(u)` là các user gần nhất và `s_uv` là similarity giữa user `u`, `v`:

```text
score_U(u,i) =
  sum_v s_uv * X[v,i] * 1[X[v,i] > 0]
  ------------------------------------------------
  sum_v s_uv * 1[X[v,i] > 0]
```

Chỉ các neighbor có tương tác với item ứng viên mới đóng góp vào mẫu số. Nếu
mẫu số bằng 0, code dùng item mean làm fallback.

### Điểm xếp hạng Item-Based

Gọi `H(u)` là lịch sử video của user `u`, `N(i)` là các item gần item ứng viên:

```text
score_I(u,i) =
  sum_j sim(i,j) * X[u,j]
  ------------------------
  sum_j sim(i,j)
```

Trong đó `j` thuộc phần giao giữa lịch sử `H(u)` và neighbor list của `i`.
Video đã xem hoặc nằm trong `excludeItemIds` được loại khỏi danh sách cuối.

## Luồng tính Top-K

```text
X
  -> tính similarity
  -> giữ tối đa neighbor_count neighbor
  -> tính score cho từng video chưa xem
  -> loại video đã xem/exclude
  -> sort giảm dần
  -> lấy K video đầu tiên
```

`neighbor_count` là số hàng xóm dùng trong công thức. `K` là số video trả về.
Hai tham số này khác nhau.

## Report runtime

Report chỉ giữ các thông tin kỹ thuật:

- `fit_seconds`: thời gian train từng biến thể User-Based/Item-Based.
- `total_fit_seconds`: tổng thời gian train các biến thể.
- thời gian dựng ma trận;
- thời gian tính similarity;
- thời gian chọn neighbor;
- tổng wall-clock time;
- peak memory ước tính;
- speedup so với baseline dense.

Các file được tạo trong `reports/<model-version>/`:

- `report.html`: report HTML tự chứa;
- `summary.md`: bản Markdown để đọc nhanh;
- `training_history.csv`: thời gian fit từng model;
- `cf_comparison.csv` và `cf_comparison.html`: so sánh runtime;
- `complexity_benchmark.csv` và `complexity_benchmark.json`;
- `complexity_cost.png`: biểu đồ thời gian và bộ nhớ.

Không dùng report runtime để kết luận model nào đề xuất phù hợp hơn. Khi HuTube
có dữ liệu thật, mức độ phù hợp cần được đo bằng phản hồi hoặc hành vi người dùng
được thu thập riêng.

## Benchmark chi phí Item-Based

Benchmark hiện tại đo chi phí dựng similarity Cosine dense trên cùng dataset:

| Chiến lược | Pair slots | Tổng thời gian | Peak ước tính | Speedup |
|---|---:|---:|---:|---:|
| Baseline dense | 1,413,721 | 4.500 s | 17.81 MiB | 1.00× |

Các tối ưu đang có:

1. Dùng NumPy và `float32` cho phép nhân ma trận vector hóa.
2. Chỉ giữ tối đa `neighbor_count` neighbor sau khi tính similarity.
3. Pearson tính theo block để giảm tensor trung gian.
4. Training chạy offline; request online chỉ load artifact và ranking.

Hướng tối ưu tiếp theo:

1. Sparse co-occurrence hoặc inverted index để bỏ qua cặp không giao nhau.
2. Min-overlap threshold để loại similarity sinh từ quá ít quan sát.
3. Lưu top-neighbor sparse thay vì full similarity khi quy mô catalog tăng.
4. Đo tải và tỷ lệ hit của cache ranking ID dùng Upstash Redis (TTL 30 giây), không giữ bản sao trong RAM tiến trình.

### Benchmark cập nhật batch với MovieLens 1M

MovieLens 1M được GroupLens phân phối dưới dạng `ratings.dat` và `movies.dat`;
loader hỗ trợ trực tiếp định dạng `::` của hai file này. Dữ liệu tải về nằm trong
`data/raw/` (đã được gitignore) vì giấy phép yêu cầu không phân phối lại dataset.

Tải và giải nén từ [GroupLens MovieLens 1M](https://files.grouplens.org/datasets/movielens/ml-1m.zip):

```powershell
$datasetRoot = "data/raw/movielens"
New-Item -ItemType Directory -Force -Path $datasetRoot | Out-Null
Invoke-WebRequest `
  -Uri "https://files.grouplens.org/datasets/movielens/ml-1m.zip" `
  -OutFile "$datasetRoot/ml-1m.zip"
Expand-Archive -LiteralPath "$datasetRoot/ml-1m.zip" -DestinationPath $datasetRoot -Force
Remove-Item -LiteralPath "$datasetRoot/ml-1m.zip"
```

Chạy benchmark để có cả mốc 100.000 và 500.000 ratings. Split 50/50 cung cấp đủ
ratings mới cho mốc 500.000:

```powershell
.\.venv\Scripts\python.exe training/benchmark_incremental_item_cf.py `
  --dataset ml1m `
  --train-fraction 0.5 `
  --repeats 1 `
  --max-batch-size 500000
```

Report gồm runtime, ước tính bộ nhớ, sai số similarity và mức khớp Top-K; batch
được lấy cộng dồn từ cùng chronological split. Có thể bỏ `--max-batch-size` để
đo toàn bộ incoming partition. Kết quả được lưu trong thư mục
`reports/item_cf_update_comparison_ml1m_<timestamp>/`.
MovieLens 1M hiện được hỗ trợ trong benchmark incremental; lệnh
`training.cli train` vẫn dùng cấu hình MovieLens 100K hiện có.

## Cấu trúc chính

```text
recommendation-service/
├── app/
│   ├── api.py
│   ├── inference.py
│   ├── model_registry.py
│   └── recommenders/
│       ├── similarity.py
│       ├── user_cf.py
│       └── item_cf.py
├── training/
│   ├── cli.py
│   ├── trainer.py
│   ├── complexity.py
│   ├── reports.py
│   ├── comparison.py
│   └── quality_gate.py
├── configs/
├── data/
├── reports/
└── tests/
```

## Chạy

Mở PowerShell tại service:

```powershell
cd F:\NgDuyLinh\Khoa_Luan_Tot_Nghiep\HuTube\recommendation-service
```

Kiểm tra dataset:

```powershell
.\.venv\Scripts\python.exe -m training.cli validate-data `
  --data-dir data/raw/movielens/ml-100k
```

Train đầy đủ sáu biến thể:

```powershell
.\.venv\Scripts\python.exe -m training.cli train `
  --config configs/movielens-100k.yaml
```

Sinh lại report runtime từ artifact:

```powershell
.\.venv\Scripts\python.exe -m training.cli report `
  --artifact data/artifacts/<model-version>
```

Xem runtime đã lưu:

```powershell
.\.venv\Scripts\python.exe -m training.cli evaluate `
  --artifact data/artifacts/<model-version>
```

Chạy kiểm thử:

```powershell
.\.venv\Scripts\python.exe -m pytest -q
.\.venv\Scripts\python.exe -m ruff check app training tests
```

## Artifact

Mỗi artifact chứa các file model, mapping, cấu hình, metadata, `metrics.json` với
runtime payload và `training_history.json`. Artifact MovieLens có
`source=MOVIELENS` và `deployable=false`, không được promote trực tiếp vào
production.

FastAPI nhận `userId`, `limit`, `excludeItemIds` và trả về `itemId`, `score`,
`modelVersion`, `source`. Backend .NET chịu trách nhiệm hydrate metadata, kiểm tra
quyền và lọc video private/deleted/blocked.

## Tích hợp HuTube và cập nhật model

Super Admin dùng `/recommendations` trên admin web. Backend .NET tạo một dòng CSV cho mỗi cặp user–video từ PostgreSQL, ghi CSV vào `collaborative_cf/YYYY/MM/DD/` trên R2, rồi gọi `POST /internal/model/train` với `X-Model-Admin-Token`, khóa CSV và SHA-256. Endpoint chỉ huấn luyện **Item-Based Cosine**. Artifact được lưu trên R2; `collaborative_cf/active.json` công bố phiên bản đang hoạt động. Khi khởi động lại, service xác minh hash artifact và nạp model từ R2 vào bộ nhớ. Model cũ tiếp tục phục vụ nếu cập nhật thất bại.

Web và mobile gọi trực tiếp `GET /api/v1/feed/recommended?page=1&pageSize=20` với `Authorization: Bearer <access-token>`. FastAPI lấy danh tính và lịch sử xem từ .NET `/api/v1/feed/recommendation-context`, chạy CF, rồi gửi thứ tự ID đến `/api/v1/feed/resolve` để dựng video cards, lọc quyền xem/video đã xem, chèn quảng bá và phân trang. Hai endpoint .NET này không gọi model; danh tính lấy từ phiên đăng nhập, không nhận `userId` từ client. Khi model chưa sẵn sàng hoặc user chưa có trong model, feed dùng video catalogue dự phòng. Nếu FastAPI không kết nối được, client gọi `/feed/resolve` với danh sách rỗng để lấy feed dự phòng.

Cấu hình FastAPI `HUTUBE_API_BASE_URL` là base API .NET (local `http://127.0.0.1:5080/api/v1`, Render `https://hutube.onrender.com/api/v1`). `CORS_ALLOWED_ORIGINS` là mảng JSON các origin User Web được phép, ví dụ `["http://localhost:4200","https://your-user-web.example"]`. Browser chỉ gửi bearer token, không gửi refresh cookie sang FastAPI.

Với các deployment hiện tại, web/mobile dùng `RECOMMENDATION_BASE_URL=https://hutube-recommendation-services.onrender.com/api/v1`, còn API .NET dùng `Recommendation__ServiceUrl=https://hutube-recommendation-services.onrender.com` (không thêm `/api/v1`). Trên service Python của Render, đặt `CORS_ALLOWED_ORIGINS=["https://hutube-beta.vercel.app","http://localhost:4200","http://127.0.0.1:4200"]` rồi lưu và triển khai lại. Biến môi trường đã đặt trên Render sẽ ghi đè mặc định trong mã nguồn; cần cập nhật cả biến cũ nếu nó chỉ chứa localhost. Nếu `/health` trả `200` nhưng `OPTIONS /api/v1/feed/recommended` trả `400 Disallowed CORS origin`, kiểm tra danh sách này. Nếu preflight trả `404` hoặc trang HTML của Render, kiểm tra lại hostname thực tế, bao gồm chữ `s` trong `services`.

Lệnh huấn luyện dùng cùng `jobId` khi thử lại lỗi proxy `502`, `504` hoặc `503` tạm thời, trong giới hạn `Recommendation__TimeoutSeconds` (mặc định 90 giây). Lỗi token hoặc cấu hình storage không được thử lại. Trang HTML của proxy không được lưu vào lỗi job hiển thị trên admin.

FastAPI chuyển IP client sang hai endpoint dữ liệu bằng header nội bộ có service token. .NET chỉ nhận IP này khi token khớp, để giữ đúng giới hạn feed theo IP và thông tin phiên đăng nhập. Render cấu hình Uvicorn `FORWARDED_ALLOW_IPS=127.0.0.1,10.0.0.0/8` để nhận IP từ ingress được tin cậy.

`POST /internal/recommendations` vẫn dành cho các tác vụ server với `X-Service-Token` riêng. Không đưa service token hoặc admin token vào config web/mobile. MovieLens và sáu biến thể CF vẫn có thể benchmark local bằng pipeline benchmark riêng.

Để huấn luyện thủ công từ CSV đã xuất, chỉ rõ file thật:

```powershell
python -m training.train_hutube --matrix-csv interactions_current.csv
```

File cần có `user_id`, `video_id`, `score`, tối thiểu 2 user, 2 video và 3 cặp. `score` phải là giá trị chuẩn hóa trong `[0,1]`; production nhận thêm `scoreAggregation` với `mode` là `average` (mặc định) hoặc `weighted` và map trọng số cho `rating`, `like`, `dislike`, `watch`, `comment`, `subscribe`. Không có dữ liệu giả hoặc tự nạp dữ liệu từ simulator cũ.

Local: cấu hình `Recommendation__ServiceUrl`, `Recommendation__ServiceToken`, `Recommendation__AdminToken` trong file `.env.local` ở thư mục gốc cùng thông tin `Storage__R2__*` cho API .NET. Chạy `./scripts/run-local.ps1 -Component api` và `./scripts/run-local.ps1 -Component recommender`. Script Recommendation Service tự nạp cùng `.env.local` và ánh xạ hai token sang `RECOMMENDER_SERVICE_TOKEN` / `RECOMMENDER_ADMIN_TOKEN`, ưu tiên hơn token trong `recommendation-service/.env`. Các biến `R2_*` vẫn đặt trong `recommendation-service/.env`. Khi chạy Uvicorn trực tiếp hoặc triển khai riêng, phải đặt `RECOMMENDER_ADMIN_TOKEN` trùng chính xác `Recommendation__AdminToken` và token suy luận cũng tương ứng. Sau khi đổi token, khởi động lại cả hai dịch vụ; không dùng token suy luận để huấn luyện. Script chỉ bind `127.0.0.1`. Trên Render, cấu hình các biến trong `render.yaml`; web miễn phí có thể ngủ và request đầu có thể rơi về feed mặc định.

### Cấu hình R2 cho Recommendation Service trên Render

Service Python cần thông tin R2 của chính nó để đọc CSV, lưu trạng thái job và tải model.
Biến đã đặt trên service API .NET không tự được chia sẻ sang service Python.
Dùng cùng bucket và thông tin truy cập với API:

| API .NET | Recommendation Service |
|---|---|
| `Storage__R2__AccountId` | `R2_ACCOUNT_ID` |
| `Storage__R2__AccessKeyId` | `R2_ACCESS_KEY_ID` |
| `Storage__R2__SecretAccessKey` | `R2_SECRET_ACCESS_KEY` |
| `Storage__R2__BucketName` | `R2_BUCKET_NAME` |

Python cũng chấp nhận tên biến `Storage__R2__*` khi dùng chung environment group.
Tên `R2_*` được ưu tiên khi cả hai kiểu tên cùng tồn tại.
Sau khi lưu biến, deploy lại Recommendation Service để tiến trình nạp cấu hình mới.

`GET /internal/model` với header `X-Model-Admin-Token` trả `storageConfigured` và
`missingStorageSettings` để đối chiếu mà không tiết lộ giá trị secret.
Nếu thiếu cấu hình, train và polling trả HTTP 503 với mã
`MODEL_STORAGE_NOT_CONFIGURED` cùng danh sách biến còn thiếu.
Health `/health` chỉ kiểm tra tiến trình đang hoạt động; HTTP 200 không xác nhận
đã có model hoặc đã cấu hình R2 đầy đủ. Lỗi chưa xử lý được ghi traceback qua
logger của Uvicorn và trả `MODEL_SERVICE_ERROR` thay cho response 500 rỗng.

## Giới hạn và Hướng phát triển tiếp theo

- Đã tích hợp hoàn tất HttpClient với Backend .NET và bảng tin Trang chủ.
- Benchmark cập nhật incremental hỗ trợ MovieLens 100K và 1M; pipeline huấn luyện dùng cấu hình MovieLens 100K hoặc dữ liệu tương tác HuTube.
- Hướng phát triển tiếp theo: Hybrid Recommendation kết hợp giữa Collaborative Filtering (CF) và Content-Based Filtering (dựa trên thể loại, tag, mô tả video).
