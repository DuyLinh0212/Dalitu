# SePay checkout

New paid-plan checkouts use a bank-transfer QR generated from the configured SePay receiving account. HuTube creates a pending payment with a unique transfer description, then activates the plan after the SePay webhook is verified and matched to that payment.

## Configuration

Set these environment variables on the API service:

```dotenv
SePay__SecretKey=...
SePay__AccountNumber=...
SePay__BankCode=...
SePay__PaymentExpiryMinutes=1440
SePay__TransactionCodePrefix=HUTUBE
```

Get the webhook secret from SePay's webhook security settings. `AccountNumber` and `BankCode` identify the receiving account used to render the VietQR image. Keep secrets in the deployment environment or `.env.local`; do not commit them.

New payments expire after 24 hours. Existing payments retain their original expiry. The EC2 compose configuration explicitly sets the 24-hour value, including when an older `.env.ec2-api` contains the previous value. The expiry worker wakes for newly committed payments and their deadlines, with a reconciliation at aligned 30-minute boundaries to recover missed signals or another instance's work.

## Webhook

Configure SePay to send payment notifications to:

```text
https://<public-api-host>/api/v1/payments/webhook/sepay
```

The API expects the signed raw-body webhook headers `X-SePay-Signature` and `X-SePay-Timestamp`. SePay must be able to reach the public HTTPS endpoint.

## Checkout behavior

- `POST /api/v1/payments` creates a pending SePay payment and returns the payment QR and transfer code.
- The user scans the QR and transfers the exact amount with the displayed transfer code.
- `POST /api/v1/payments/webhook/sepay` verifies and matches the notification; a browser return alone does not activate the plan.
- The web and mobile payment dialogs poll the payment status while the transfer is pending.
