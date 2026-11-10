namespace HuTube.Infrastructure.Payments;

/// <summary>
/// Cấu hình tích hợp SePay. Điền vào .env.local sau khi tạo webhook trên my.sepay.vn.
/// </summary>
public sealed class SepayOptions
{
    /// <summary>
    /// Secret key HMAC-SHA256 lấy từ trang cấu hình Webhook trên my.sepay.vn (tab Bảo mật).
    /// Ví dụ: SePay__SecretKey=abc123xyz...
    /// </summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// Số tài khoản ngân hàng thụ hưởng. Ví dụ: "1017588888"
    /// </summary>
    public string AccountNumber { get; set; } = "";

    /// <summary>
    /// Tên ngắn ngân hàng dùng cho VietQR. Ví dụ: "Vietcombank", "BIDV", "MBBank"
    /// </summary>
    public string BankCode { get; set; } = "";

    /// <summary>
    /// Thời gian hết hạn của một đơn hàng chờ thanh toán (phút). Mặc định 24 giờ.
    /// </summary>
    public int PaymentExpiryMinutes { get; set; } = 1440;

    /// <summary>
    /// Prefix cho mã nội dung chuyển khoản. Mặc định "HUTUBE".
    /// Mã đầy đủ sẽ là "HUTUBE-XXXXXXXX".
    /// </summary>
    public string TransactionCodePrefix { get; set; } = "HUTUBE";
}
