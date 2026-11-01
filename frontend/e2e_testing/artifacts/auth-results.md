# Kết quả đăng ký và đăng nhập Playwright thật

Ngày chạy: 02/10/2026, múi giờ Asia/Bangkok (run ID dùng UTC). Chromium/Chrome headless, cả hai Angular production build, Kestrel + PostgreSQL riêng; email Pickup. Không mock API. Account cố định `hutube.e2e.owner@example.test`.

| Run ID | PASS | REUSED | FAIL | Browser (giây) | Account sau run | Register 201 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `20261002T050603.730539Z` | 5 | 0 | 1 | 11.922 | 1 | 1 |
| `20261002T050803.987210Z` | 11 | 3 | 0 | 16.734 | 1 | 0 |
| `20261002T070511.888580Z` | 11 | 3 | 0 | 41.500 | 1 | 0 |
| `20261002T072845.372557Z` | 11 | 3 | 0 | 25.844 | 1 | 0 |

Run đầu đã đăng ký 201, login unverified bị từ chối và verify email thật thành công, sau đó dừng do test kỳ vọng sai tên code lỗi duplicate. Kỳ vọng đã sửa theo contract `EMAIL_ALREADY_EXISTS`/`USERNAME_ALREADY_EXISTS`; các run tiếp theo dùng checkpoint, không tạo user mới. Đây là lỗi kỳ vọng của harness, không có chỉnh sửa ứng dụng trong bộ E2E này.

REUSED gồm AUTH-REG-03, AUTH-LOGIN-01 và AUTH-VERIFY-01: tiền điều kiện đã hoàn thành ở run đầu, giữ bằng chứng đầu và không đăng ký/xác minh lại. 11 case còn lại thực thi PASS trong mỗi warm run; JavaScript pageerror bằng 0. Sau cuối flow role admin tạm được khôi phục. Database được giữ, dịch vụ runner tự dừng.

| Case | Bằng chứng thực thi đầu | Trạng thái run mới nhất |
| --- | --- | --- |
| `AUTH-REG-01` | `20261002T050603.730539Z` | PASSED |
| `AUTH-REG-02` | `20261002T050603.730539Z` | PASSED |
| `AUTH-REG-03` | `20261002T050603.730539Z` | REUSED |
| `AUTH-LOGIN-01` | `20261002T050603.730539Z` | REUSED |
| `AUTH-VERIFY-01` | `20261002T050603.730539Z` | REUSED |
| `AUTH-REG-04` | `20261002T050803.987210Z` | PASSED |
| `AUTH-LOGIN-02` | `20261002T050803.987210Z` | PASSED |
| `AUTH-LOGIN-03` | `20261002T050803.987210Z` | PASSED |
| `AUTH-LOGIN-04` | `20261002T050803.987210Z` | PASSED |
| `AUTH-LOGIN-05` | `20261002T050803.987210Z` | PASSED |
| `AUTH-LOGIN-06` | `20261002T050803.987210Z` | PASSED |
| `AUTH-ADMIN-01` | `20261002T050803.987210Z` | PASSED |
| `AUTH-ADMIN-02` | `20261002T050803.987210Z` | PASSED |
| `AUTH-ADMIN-03` | `20261002T050803.987210Z` | PASSED |

## Ảnh minh chứng

Ảnh run được lưu theo cấu trúc `screenshots/<ngày-run>/Auth/<user|admin>/<case-id>/<step>.png`; baseline dùng `screenshots/baseline-auth/Auth/<user|admin>/<case-id>/<step>.png`. Thư mục ngày kèm giờ UTC để phân biệt các lượt chạy trong cùng ngày.

| Phạm vi | Module | Flow | Case | Screenshot | Run nguồn |
| --- | --- | --- | --- | --- | --- |
| admin | authentication | access_denied | AUTH-ADMIN-01 | [15-admin-normal-user-denied.png](../screenshots/baseline-auth/Auth/admin/AUTH-ADMIN-01/15-admin-normal-user-denied.png) | `20261002T072845.372557Z` |
| admin | authentication | login | AUTH-ADMIN-02 | [16-admin-login-success-desktop.png](../screenshots/baseline-auth/Auth/admin/AUTH-ADMIN-02/16-admin-login-success-desktop.png) | `20261002T072845.372557Z` |
| admin | authentication | login | AUTH-ADMIN-02 | [17-admin-login-success-mobile.png](../screenshots/baseline-auth/Auth/admin/AUTH-ADMIN-02/17-admin-login-success-mobile.png) | `20261002T072845.372557Z` |
| admin | authentication | logout | AUTH-ADMIN-03 | [18-admin-logout-guard.png](../screenshots/baseline-auth/Auth/admin/AUTH-ADMIN-03/18-admin-logout-guard.png) | `20261002T072845.372557Z` |
| user | authentication | email_verification | AUTH-VERIFY-01 | [07-email-verification-success.png](../screenshots/baseline-auth/Auth/user/AUTH-VERIFY-01/07-email-verification-success.png) | `20261002T050603.730539Z` |
| user | authentication | login | AUTH-LOGIN-01 | [06-login-unverified-denied.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-01/06-login-unverified-denied.png) | `20261002T050603.730539Z` |
| user | authentication | login | AUTH-LOGIN-02 | [09-login-required-fields.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-02/09-login-required-fields.png) | `20261002T072845.372557Z` |
| user | authentication | login | AUTH-LOGIN-03 | [10-login-wrong-password.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-03/10-login-wrong-password.png) | `20261002T072845.372557Z` |
| user | authentication | login | AUTH-LOGIN-04 | [11-user-login-success-desktop.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-04/11-user-login-success-desktop.png) | `20261002T072845.372557Z` |
| user | authentication | login | AUTH-LOGIN-04 | [12-user-login-success-mobile.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-04/12-user-login-success-mobile.png) | `20261002T072845.372557Z` |
| user | authentication | login | AUTH-LOGIN-05 | [13-user-session-restored.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-05/13-user-session-restored.png) | `20261002T072845.372557Z` |
| user | authentication | logout | AUTH-LOGIN-06 | [14-user-logout-guard.png](../screenshots/baseline-auth/Auth/user/AUTH-LOGIN-06/14-user-logout-guard.png) | `20261002T072845.372557Z` |
| user | authentication | registration | AUTH-REG-01 | [01-register-required-fields.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-01/01-register-required-fields.png) | `20261002T072845.372557Z` |
| user | authentication | registration | AUTH-REG-02 | [02-register-password-mismatch.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-02/02-register-password-mismatch.png) | `20261002T072845.372557Z` |
| user | authentication | registration | AUTH-REG-03 | [03-register-filled-desktop.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-03/03-register-filled-desktop.png) | `20261002T072845.372557Z` |
| user | authentication | registration | AUTH-REG-03 | [04-register-filled-mobile.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-03/04-register-filled-mobile.png) | `20261002T072845.372557Z` |
| user | authentication | registration | AUTH-REG-03 | [05-register-success.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-03/05-register-success.png) | `20261002T050603.730539Z` |
| user | authentication | registration | AUTH-REG-04 | [08-register-duplicate-denied.png](../screenshots/baseline-auth/Auth/user/AUTH-REG-04/08-register-duplicate-denied.png) | `20261002T072845.372557Z` |

Ảnh được copy nguyên bản từ Playwright, password input được mask; không lấy từ ảnh dựng hoặc UI mock. Baseline chỉ có account synthetic. Report/trace/log đầy đủ ở `artifacts/runs/<run-id>` local bị gitignore; ảnh run mới ở `screenshots/<ngày-run>/Auth/<user|admin>/<case-id>`. Không publish state, mail token hay trace chứa cookie/password. JSON đã lọc ở [auth-evidence.json](auth-evidence.json).

Kết quả này chỉ chứng minh các case auth liệt kê. Chưa chạy full chức năng Web, upload/transcode, OAuth/R2/SePay thật hoặc performance. Steps, dữ liệu và expected của full suite ở hai tài liệu trong `docs`; hướng dẫn chạy ở [README](../README.md).
