# Event Contract — Thống nhất giữa Trip Service và Payment Service

**Người phụ trách:** Sơn — Messaging / QA / DevOps (F)

**Mục đích:** Thống nhất tên, cấu trúc dữ liệu và ý nghĩa của các event được truyền giữa các service thông qua RabbitMQ/MassTransit.

## 1. Event Contract là gì?

Event Contract là quy định chung về cấu trúc và ý nghĩa của các thông điệp được trao đổi giữa các service.

Ví dụ, khi tài xế trả khách xong, Trip Service phát event `RideCompleted` để Payment Service biết chuyến nào đã hoàn thành và cần được thanh toán.

**Ví dụ dữ liệu của RideCompleted (đề xuất, chưa chốt):**

```json
{
  "eventId": "6e19426e-04f5-4903-868e-f03f3d58bf12",
  "tripId": "ca486d82-4883-4382-9ab7-aee8c75a1653",
  "amount": 95000,
  "currency": "VND",
  "occurredAt": "2026-10-12T06:00:00Z",
  "correlationId": "095eb521-ebec-4219-8c41-21417216497c"
}
```

Trong tài liệu kiến trúc có đề cập đến `RideCompleted` và `PaymentSettled`, nhưng chưa quy định chi tiết các trường dữ liệu trong DTO. Vì vậy, phần này cần được thống nhất trước khi triển khai.

## 2. Các event cần thống nhất

| Event | Service gửi | Service nhận | Thời điểm |
|---|---|---|---|
| `RideCompleted` | Trip | Payment | Driver đã trả khách, chuyến hoàn thành |
| `PaymentSettled` | Payment | Trip | Thanh toán và ghi nhận giao dịch thành công |
| `PaymentFailed` | Payment | Trip | Thanh toán thất bại sau khi hết cơ chế retry theo thỏa thuận |

**Đề xuất:** Triển khai `RideCompleted` và `PaymentSettled` trước. `PaymentFailed` sẽ bổ sung sau khi nhóm thống nhất cách xử lý thanh toán lỗi.

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