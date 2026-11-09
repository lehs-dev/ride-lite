# Event Contract — Thống nhất giữa Trip Service và Payment Service

**Người phụ trách:** Sơn — Messaging / QA / DevOps (F)

**Mục đích:** Thống nhất tên, cấu trúc dữ liệu và ý nghĩa của các event được truyền giữa các service thông qua RabbitMQ/MassTransit.

## 1. Event Contract là gì?

Event Contract là quy định chung về cấu trúc và ý nghĩa của các thông điệp được trao đổi giữa các service.

Ví dụ, khi tài xế trả khách xong, Trip Service phát event `RideCompleted` để Payment Service biết chuyến nào đã hoàn thành và cần được thanh toán.

**Ví dụ dữ liệu của RideCompleted (đã triển khai — xem §2.1 để đủ tập trường):**

```json
{
  "eventId": "6e19426e-04f5-4903-868e-f03f3d58bf12",
  "tripId": "ca486d82-4883-4382-9ab7-aee8c75a1653",
  "riderId": "9c0d5f2e-2b1f-4c3a-8a3f-2b6f0d9c1e44",
  "amount": 95000,
  "currency": "VND",
  "paymentRequestKey": "trip:ca486d82488343829ab7aee8c75a1653",
  "occurredAtUtc": "2026-10-12T06:00:00Z",
  "correlationId": "095eb521-ebec-4219-8c41-21417216497c",
  "schemaVersion": 1
}
```

Tập trường chính thức của cả ba event đã được chốt và triển khai — xem **§2.1**.

## 2. Các event cần thống nhất

| Event | Service gửi | Service nhận | Thời điểm |
|---|---|---|---|
| `RideCompleted` | Trip | Payment | Driver đã trả khách, chuyến hoàn thành |
| `PaymentSettled` | Payment | Trip | Thanh toán và ghi nhận giao dịch thành công |
| `PaymentFailed` | Payment | Trip | Thanh toán thất bại sau khi hết cơ chế retry theo thỏa thuận |

**Đã chốt (2026-11-04):** cả ba event đều triển khai. `PaymentFailed` không còn "deferred" — xem §2.1 để biết ngữ nghĩa và tập trường.

### 2.1 Hợp đồng đã chốt

Cả ba DTO sống trong `src/Contracts/Events/`, dùng chung qua project reference. Idempotency key
là `(TripId, PaymentRequestKey)` chứ không phải `EventId` (§3.4). `PaymentRequestKey` do
`EventContracts.BuildPaymentRequestKey(tripId)` sinh ra, giá trị `trip:{tripId:N}` — hai bên đều
phải dùng đúng hàm này, vì TripService từ chối `PaymentSettled` mang key khác key đã lưu lúc dropoff.

| Event | Trường | Ngữ nghĩa |
|---|---|---|
| `RideCompleted` | EventId, TripId, RiderId, DriverId?, Amount, Currency, OccurredAtUtc, CorrelationId?, PaymentRequestKey, SchemaVersion | Trip đã trả khách, số tiền do server chốt. PaymentService thu tiền. |
| `PaymentSettled` | EventId, TripId, PaymentId, TransactionId?, PaymentRequestKey, Amount, Currency, OccurredAtUtc, CorrelationId?, SchemaVersion | Đã thu tiền thành công. TripService chuyển `PaymentPending → Paid`. |
| `PaymentFailed` | EventId, TripId, PaymentId, PaymentRequestKey, ReasonCode, RetryExhausted, OccurredAtUtc, CorrelationId?, AttemptCount, SchemaVersion | Payment đạt trạng thái `Failed` vĩnh viễn: gateway từ chối không retryable, hoặc đã đụng trần retry. `AttemptCount` = số lần retry đã dùng (mặc định 0 với lỗi từ chối ngay lần đầu; payload schema 1 thiếu field vẫn đọc được). |

Quy tắc xử lý `PaymentFailed` phía Trip (M5-08):

- Trip **giữ nguyên `PaymentPending`**; không đánh dấu `Paid`, không hoàn tác việc đã chở khách.
- Ghi một `TripStatusHistory` (`ActorType=Consumer`, `FromStatus=ToStatus=PaymentPending`,
  `Reason` chứa `ReasonCode`) — mã lỗi đầy đủ nằm ở `Payment.FailureCode` trong PayDB.
- Nếu trip đã `Paid` thì event đến muộn là no-op có chủ đích: **không bao giờ hạ cấp** `Paid`.
- Không có API public để client tự đánh dấu Paid/Failed. Rider/admin phục hồi bằng
  `POST /payments/{id}/retry` hoặc `POST /payments/trip/{tripId}/retry` (retry theo trip khi
  chưa biết payment id; chỉ owner hoặc Admin), payment được mở lại với hạn mức retry mới.
  Admin có thêm `POST /admin/payments/{id}/retry`.
- Event chỉ được phát khi payment thật sự chốt `Failed`. Kết quả trung gian
  (`Created`/`Charging`/`PendingRetry`) **không** được claim inbox, để redelivery tiếp tục được việc.

Hàng đợi tiêu thụ: `payment-service.ride-completed`, `trip-service.payment-settled`,
`trip-service.payment-failed`. Event thất bại rơi vào `_error` thay vì bị ACK im lặng.

## 3. Những vấn đề cần B và E xác nhận

**1. Kiểu dữ liệu của TripId**

- Đang sử dụng `Guid` hay kiểu khác?
- Sơn đề xuất `Guid` để thống nhất giữa các service.

**2. Giá trị Amount trong RideCompleted**

- Là giá được Rider xác nhận lúc đặt xe hay tổng tiền cuối cùng sau chuyến đi?
- Trip Service sẽ cung cấp Amount hay Payment Service tự tính?
- Sơn đề xuất Trip Service gửi số tiền cuối cùng đã được xác định theo nghiệp vụ, Payment Service chịu trách nhiệm xử lý thanh toán.

**3. Đơn vị tiền tệ**

- Chỉ sử dụng VND hay hỗ trợ nhiều loại tiền?
- Sơn đề xuất chỉ dùng VND trong phạm vi bài tập để đơn giản hóa.

**4. Xử lý sự kiện trùng lặp (Idempotency)**

- Nếu Payment Service nhận lại `RideCompleted` của cùng một chuyến, phải bảo đảm không tạo thêm thanh toán trùng.
- Sơn đề xuất Payment Service dùng `TripId` kết hợp quy tắc thanh toán để kiểm tra, không chỉ dựa vào `EventId`.
- Nhờ E xác nhận cách xử lý phần này.

**5. Xác nhận luồng xử lý**

- Khi Trip chuyển sang `DroppedOff`, B sẽ publish `RideCompleted`.
- E nhận `RideCompleted`, tiến hành xử lý thanh toán.
- Nếu thành công, E publish `PaymentSettled`.
- B nhận `PaymentSettled` và cập nhật Trip thành `Paid`.

Nhờ B và E xác nhận luồng này có phù hợp với nghiệp vụ hai service đang triển khai không.

## 4. Sau khi thống nhất

Sơn sẽ tiến hành:

- Tạo các Event DTO trong `src/Contracts/Events/`.
- Viết test kiểm tra serialization/deserialization.
- Tạo Pull Request để B và E review.
- Sau đó tiếp tục cấu hình MassTransit và kiểm thử giao tiếp RabbitMQ giữa hai service.