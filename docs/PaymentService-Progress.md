# PaymentService — tiến trình & hướng dẫn kiểm thử

Tài liệu này mirror cấu trúc `TripService-Progress.md`, ghi lại những gì đã code cho luồng thanh toán event-driven (R06/R07, M5-02..M5-08).

## 1. Phạm vi đã triển khai

| Yêu cầu | Hiện trạng | Bằng chứng |
|---|---|---|
| R06 — trả khách kích hoạt thanh toán, chỉ đóng khi đã trả tiền | ✅ | `POST /trips/{id}/dropoff` → outbox `RideCompleted` → consumer Payment settle → consumer Trip `PaymentPending → Paid`. E2E: trip `cd89f23b…` đạt `Paid`, PayDB 1 row `Status=Settled`. |
| R07 — thanh toán lỗi/chờ xử lý, retry giới hạn, không trừ tiền lặp | ✅ | Magic trigger theo chữ số tận cùng amount: `…3` → `Failed(INSUFFICIENT_FUNDS)`; `…7` → `PendingRetry(GATEWAY_UNAVAILABLE)`, worker retry tới `PaymentPolicy.MaxGatewayAttempts=3` rồi `Failed`. Duplicate RideCompleted republish qua broker → vẫn 1 row `Payments`, 1 inbox row. |
| M5-02 MassTransit/RabbitMQ config | ✅ | `AddMassTransit` + `UsingRabbitMq` ở cả 2 service; queue đặt tên tường minh `payment-service.ride-completed`, `trip-service.payment-settled` (ConsumerDefinition.EndpointName). |
| M5-03 một publisher abstraction trong Application | ✅ | `ITripEventPublisher` / `IPaymentEventPublisher`; Domain/Application không reference MassTransit. |
| M5-04 dropoff hợp lệ → history DroppedOff + PaymentPending + intent RideCompleted cùng transaction | ✅ | `TripAppService.MarkDroppedOffAsync` (history x2 + outbox row, 1 SaveChanges). |
| M5-05 durable outbox giữ EventId/payment key | ✅ | Bảng `TripOutboxMessages`/`PaymentOutboxMessages`, dispatcher gửi với `MessageId` cố định = EventId. |
| M5-06 consumer PaymentSettled kiểm tra TripId/key/amount/currency, chỉ PaymentPending→Paid | ✅ | `PaymentSettledConsumer`: verify amount+currency, `TryMarkPaid` throw nếu sai trạng thái/key; đã Paid → no-op idempotent. Không có API public đánh dấu Paid. |
| M5-07 durable inbox dedup cùng transaction | ✅ | Bảng `ProcessedEvents` PK `(ConsumerName, BusinessKey)`, key = `{TripId}:{PaymentRequestKey}` (không dùng EventId đơn lẻ). |
| M5-08 PaymentFailed giữ trip pending | ✅ | PaymentService phát khi gateway từ chối không retryable hoặc hết lượt retry (`PaymentProcessor.FailAsync`); TripService có `PaymentFailedConsumer` (queue `trip-service.payment-failed`) ghi `TripStatusHistory` và **giữ** `PaymentPending`, không hạ cấp trip đã `Paid`. Recovery: `PaymentBackgroundWorker` quét `PendingRetry` + `Created` mồ côi + `Charging` quá lease. Test: `PaymentFailedConsumerTests`, `PaymentStoreTests`, `PaymentProcessorTests`. |

## 2. Quyết định kiến trúc

- **MassTransit pin `8.5.12`** (Apache-2.0). v9 là sản phẩm thương mại yêu cầu license key — không dùng cho đồ án; ai `dotnet add package` không version sẽ dính v9.
- **Outbox/Inbox tự viết tay** thay vì `MassTransit.EntityFrameworkCore`: dedup theo khóa nghiệp vụ `TripId + PaymentRequestKey` (yêu cầu của doc), ít dependency, dễ chứng minh bằng SQL trong báo cáo. Dispatcher: Postgres `FOR UPDATE SKIP LOCKED`, SQL Server `WITH (UPDLOCK, READPAST)`.
- **Schema**: PayDB dùng EF Core migrations (`InitialPayDb`); TripDB giữ `EnsureCreated` dev-only vì DB đang tồn tại tạo từ EnsureCreated — trộn migration lên schema đó là bẫy đã ghi ở TripService-Progress M1-03/M1-08.
- **Consistency trade-off (đã sửa P0-1/P0-2, 2026-11-04):** trước đây consumer ghi inbox row + payment `Created` trong MỘT transaction rồi mới gọi gateway. Crash sau bước đó khiến redelivery thấy inbox → ACK mù → payment kẹt `Created` vĩnh viễn, và không có worker nào cứu (nó chỉ quét `PendingRetry`). Ngoài ra không có cơ chế đồng thời nào, nên REST + worker + hai instance có thể cùng charge. Cơ chế hiện tại:
  - **Inbox chỉ được claim khi nghĩa vụ đạt kết quả chốt** (`Settled`/`Failed`/đã chốt từ trước). `Created`/`Charging`/`PendingRetry` không claim, nên redelivery luôn làm tiếp được việc.
  - **Claim bằng UPDATE đổi `Status`** (`Created|PendingRetry → Charging`), `Status` là concurrency token nên đúng một bên thắng; bên thua trả `InFlightElsewhere` và không gọi gateway. Đã thử `xmin`/`.IsRowVersion()` và loại: EF sinh `ALTER TABLE ... ADD COLUMN "xmin" xid`, PostgreSQL từ chối vì `xmin` là system column.
  - **`PaymentBackgroundWorker` là đường recovery thật**: quét `PendingRetry` tới hạn, `Created` mồ côi quá `OrphanedCreatedGrace`, và `Charging` quá `ChargingLease` (process chết giữa gateway call và lần ghi kết quả) — lease được reclaim về `PendingRetry` rồi mới charge lại, nên không bao giờ có hai phiên cùng charge.
  - Vẫn là **at-least-once + idempotency**, không tuyên bố exactly-once: sandbox sinh `OrderRef` cố định theo TripId nên gọi lại không tạo giao dịch mới; phía Trip dedup theo `(TripId, PaymentRequestKey)`.
- **MoMo sandbox**: mock đồng bộ + latency giả lập, KHÔNG làm IPN webhook (phải mở endpoint anonymous public, thêm attack surface, không tăng bằng chứng requirement). Refund mô phỏng bất đồng bộ: `202 Accepted` → `PaymentBackgroundWorker` gọi gateway → `Refunded` (terminal). **Phạm vi refund: MỘT refund toàn phần cho một payment**, chặn bằng unique index filtered `("PaymentId") WHERE "Status" <> 4` — giữ chỗ cho row Rejected để refund fail vẫn hoàn lại được. Gateway lỗi thì payment trở về `Settled` (`MarkRefundRejected`), không còn kẹt `RefundPending`. Checksum refund tính theo **số tiền giao dịch gốc** (`RefundIntent.OriginalAmount`), khớp cách charge ký; refund một phần bị chặn từ domain.
- **Cước do server chốt**: `EstimatedPrice = BaseFareVnd(12.000) + DistanceKm × PerKmVnd(13.500)`, haversine, làm tròn 500 VND (config `TripPricing:*`). Client không POST được giá.

## 3. Sandbox triggers (dùng cho demo)

| Số tiền (VND) | Kết quả |
|---|---|
| bất kỳ chia hết 500 thông thường | `SUCCESS` → Settled |
| ≤ 0 hoặc ≠ VND | `INVALID_AMOUNT` → Failed |
| > 5.000.000 | `AMOUNT_LIMIT_EXCEEDED` → Failed |
| chữ số tận cùng = **3** (vd 213.003) | `INSUFFICIENT_FUNDS` → Failed (non-retryable), phát `PaymentFailed` với `RetryExhausted=false` |
| chữ số tận cùng = **7** (vd 137.007) | `GATEWAY_UNAVAILABLE` → PendingRetry, worker retry tới `MaxGatewayAttempts` rồi Failed + phát `PaymentFailed` với `RetryExhausted=true` |

Lưu ý: fare luôn làm tròn 500 nên nhánh `…3/…7` chỉ kích hoạt được khi publish event thủ công (management UI/API) — dùng cho demo idempotency & retry.

## 4. Runbook kiểm thử thủ công

```bash
# 0. Infra (từ repo root). Lưu ý: port 1433 có thể bị SQL Server local chiếm —
#    .env hỗ trợ MSSQL_HOST_PORT (devcontainer hiện chạy compose sqlserver ở 1434).
cp .devcontainer/.env.example .devcontainer/.env   # điền 4 password
docker compose -f .devcontainer/compose.yaml up -d postgres rabbitmq sqlserver

# 1. Schema PayDB
dotnet ef database update --project src/PaymentService --context PaymentDbContext

# 2. Chạy service — THỨ TỰ QUAN TRỌNG: consumer (Payment) trước producer (Trip)
#    để queue tồn tại trước publish đầu tiên.
ConnectionStrings__PaymentDb="Host=localhost;Port=5432;Database=PayDB;Username=swiftdev;Password=<...>" \
RabbitMq__Host=localhost RabbitMq__UserName=swiftdev RabbitMq__Password=<...> \
dotnet run --project src/PaymentService --no-launch-profile --urls http://localhost:5003

ConnectionStrings__TripDb="Server=localhost,<MSSQL_HOST_PORT>;Database=TripDB;User Id=sa;Password=<...>;Encrypt=True;TrustServerCertificate=True" \
RabbitMq__Host=localhost RabbitMq__UserName=swiftdev RabbitMq__Password=<...> \
dotnet run --project src/TripService --no-launch-profile --urls http://localhost:5001

dotnet run --project src/ApiGateway --no-launch-profile --urls http://localhost:5000
```

Luồng happy path (qua Gateway :5000, token từ `/auth/login`):
1. rider: `POST /trips` → `Requested`, fare server tính.
2. driver: `driver-accept` → `en-route` → `pickup` → `dropoff` → `PaymentPending`.
3. ~2s sau: TripDB `Trips.Status=Paid`; PayDB 1 row `Payments Status=Settled`, checksum populated; `ProcessedEvents` mỗi bên 1 row.
4. admin: `GET /admin/payments?status=Settled` → dữ liệu thật từ PayDB.
5. `POST /payments/{id}/refund` → 202 → poll `GET /payments/{id}` → `Refunded`; refund lần 2 → 409 (unique index filtered), refund một phần → 400. Chỉ owner hoặc Admin được refund.
6. Refund bị gateway từ chối → payment trả về `Settled` (không kẹt `RefundPending`) và hoàn lại được.
7. Payment `Failed` (amount tận cùng `3`) → TripService nhận `PaymentFailed`, trip **vẫn** `PaymentPending` + có `TripStatusHistory` chứa mã lỗi; rider phục hồi bằng `POST /payments/{id}/retry` (reset AttemptCount, charge lại, rồi `Paid`).
8. ép nhánh kẹt: giết process PaymentService ngay sau khi claim (payment ở `Charging`) → restart → sau `ChargingLease` (90s) worker reclaim và chốt `Settled`, chỉ một lần charge.

Kiểm chứng idempotency: republish cùng payload `RideCompleted` vào exchange
`SwiftRide.Contracts.Events:RideCompleted` (RabbitMQ mgmt UI :15672, payload phải bọc
envelope `{"message":{...},"messageType":["urn:message:SwiftRide.Contracts.Events:RideCompleted"], content_type application/vnd.masstransit+json}`) → vẫn 1 payment, 1 inbox row.

Broker outage: `docker compose stop rabbitmq` → dropoff vẫn HTTP OK, outbox tích `DeliveryAttempts`; start lại → tự deliver, không sửa DB tay.

Rate limit gateway: policy `payment` = 5 req/60s/sub — spam `POST /payments` sẽ gặp 429, không phải bug.

## 5. Tests

`tests/PaymentService.Tests` (83) + `tests/TripService.Tests` (34) + Security (8) — toàn bộ xanh với `dotnet test SwiftRide.sln` (đã nghiệm thu Release, 125/125).

- Unit thuần CI-safe: state machines Payment/Trip/Refund, `MoMoSandboxGateway` từng rule, serialization events, `PaymentAppService` (EF InMemory).
- Integration cần Docker (Testcontainers, tự bật ở local; CI GitHub không có Docker nên nhóm này fail trên CI nếu chạy mặc định — filter `FullyQualifiedName!~Consumers` cho CI):
  - Chạy trong container devcontainer (docker-outside-of-docker) thì cần mount socket và trỏ Testcontainers về host machine, nếu không container test sẽ không nối được DB vừa khởi động:
    `-v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -e TESTCONTAINERS_RYUK_DISABLED=true --add-host=host.docker.internal:host-gateway`.
    Ở môi trường đó `dotnet test` in ra kết quả rỗng; `dotnet vstest <dll>` mới in tổng kết.
  - `RideCompletedConsumerTests` — Postgres 18.6 container: settle, duplicate delivery, amount conflict → `_error`, insufficient funds terminal, và **redelivery sau crash trước khi chốt vẫn xử lý tiếp** (P0-1) mà không charge lặp.
  - `PaymentStoreTests` — Postgres 18.6 container: hai phiên claim song song chỉ gọi gateway một lần (P0-2), `Charging` quá lease được worker reclaim, `Created` mồ côi được worker cứu, unique index filtered chặn refund lặp và nhả chỗ khi refund bị Rejected (P0-4).
  - `PaymentSettledConsumerTests` — SQL Server 2022 container: Paid đúng một lần, duplicate không sinh history thứ hai, mismatch amount không downgrade.
  - `PaymentFailedConsumerTests` — SQL Server 2022 container: ghi lý do mà vẫn giữ `PaymentPending`, duplicate chỉ một history, event đến sau khi Paid không hạ cấp, trip lạ không bị claim inbox (P0-3).
  - `RbacRegressionTests` — SQL Server 2022 container: seed phủ toàn bộ `PermissionsCatalog`, Rider có `payments.read`, Admin có `payments.admin`, seed lặp không nhân đôi, và đăng ký công khai không tự cấp Admin (P0-5).

## 6. File chính

- Contracts: `src/Contracts/Events/{RideCompleted,PaymentSettled,PaymentFailed,EventConstants}.cs`
- PaymentService: `Program.cs`, `Domain/Entities/*`, `Application/{Abstractions,DTOs,Services}/*`, `Infrastructure/{Persistence,Payment,Messaging,Services}/*`, `Api/{PaymentEndpoints,AdminPaymentEndpoints}.cs`, `Migrations/*_InitialPayDb.cs`
- TripService: `Domain/Entities/{Trip,TripStatus,TripStatusHistory,OutboxMessage,ProcessedEvent}.cs`, `Application/Services/TripAppService.cs`, `Api/TripEndpoints.cs`, `Infrastructure/{Messaging,Pricing,Persistence}/*`
- Config: `appsettings.json` 2 service thêm `RabbitMq` (+ `ConnectionStrings:PaymentDb`, `MoMoSandbox`, `TripPricing`); `.devcontainer/compose.yaml` tham số hóa `MSSQL_HOST_PORT`.

## 7. Việc còn treo (cho member khác / đợt sau)

- ~~Chọn phương thức thanh toán vẫn chưa có đường đi thật~~ (P1-1 ĐÃ SỬA): `POST /payments/{tripId}` và `PATCH /payments/{id}/method` tôn trọng method client gửi khi nghĩa vụ còn mở; consumer giữ method đã chốt trên obligation.
- ~~API payment còn thiếu~~ (P1-2 ĐÃ SỬA): thêm `GET /payments/trip/{tripId}`, `GET /payments/{id}/refunds`, `GET /admin/payments/{id}`, filter `tripId/riderId`, validate `from <= to`; list trả `PagedResult<T>` kèm `TotalCount`; gateway tách route GET (policy `payment-read` 60/60s) khỏi POST/PATCH (5/60s).
- ~~`PaymentDbInitializer` dùng `EnsureCreated`~~ (P1-3 ĐÃ SỬA): baseline `__EFMigrationsHistory` cho DB dev cũ rồi `MigrateAsync`; `/health/ready` kiểm tra PostgreSQL + RabbitMQ (cả PaymentService lẫn TripService); global ProblemDetails qua `UseSwiftRideProblemDetails`.
- ~~`TripResponse` chưa có payment status/failure code~~ (P1-4 ĐÃ SỬA): read model `IPaymentReadModelClient` gọi `GET /payments/trip/{tripId}` nội bộ khi trip `PaymentPending` (best-effort, cấu hình `PaymentReadModel:BaseUrl`); `TryMarkPaid` no-op an toàn với event đến muộn cho trip Cancelled/Failed.
- Ledger/payout cho driver: KHÔNG thuộc phạm vi môn học — README đã sửa thành "Thanh toán, hoàn tiền và audit" (P1-5 chốt hướng docs, không làm ledger).
- MatchingService chưa đụng tới; `/quote` vẫn 501. Fare hiện tính nội bộ TripService.
- CI: cân nhắc tách step `dotnet test --filter FullyQualifiedName!~Consumers` (unit) và job có Docker (integration).
- N+1 query trong `AuthService.GetUsersAsync` (tồn tại từ trước, không thuộc phạm vi này).

## 8. P0 đã đóng (2026-11-04)

| Lỗi | Sửa ở đâu | Bằng chứng |
|---|---|---|
| P0-1 inbox claim trước khi xong → payment kẹt `Created` | `PaymentProcessor` (claim inbox chỉ ở kết quả chốt) + `PaymentBackgroundWorker` quét `Created` mồ côi và `Charging` quá lease | `PaymentProcessorTests.ResolveObligation_AloneDoesNotClaimInbox`, `TransientGatewayError_LeavesInboxUnclaimedSoRedeliveryResumes`, `RideCompletedConsumerTests.RedeliveryAfterCrashBeforeTerminalResult_ResumesAndSettlesOnce`, `PaymentStoreTests.OrphanedCreatedPayment_IsPickedUpByWorker` |
| P0-2 không kiểm soát đồng thời → double charge | Concurrency token trên `Status` + claim `Created\|PendingRetry → Charging`; một đường charge duy nhất qua `PaymentProcessor` | `PaymentStoreTests.TwoParallelClaimants_ChargeExactlyOnce`, `PaymentProcessorTests.ChargingPayment_IsNotClaimableTwice` |
| P0-3 `PaymentFailed` chưa phát và chưa tiêu thụ | `EnqueuePaymentFailed` trong cùng transaction với `MarkFailed`; `PaymentFailedConsumer` phía Trip; `POST /payments/{id}/retry` | `PaymentProcessorTests.NonRetryableFailure_PublishesPaymentFailed`, `ExhaustedRetries_FailsWithoutCallingGatewayAndMarksExhausted`, `PaymentFailedConsumerTests` |
| P0-4 refund kẹt `RefundPending`, partial sai checksum, over-refund | `MarkRefundRejected` (về `Settled`), `RefundIntent.OriginalAmount` + checksum theo amount gốc, unique index filtered, refund có AttemptCount/backoff/lease, mỗi refund một try/catch | `PaymentRefundTests.RejectedRefund_LetsPaymentReturnToSettledAndRefundAgain`, `MoMoSandboxGatewayTests.Refund_PartialAgainstFullOriginal_InvalidAmount`, `PaymentStoreTests.SecondActiveRefundForSamePayment_IsBlockedByUniqueIndex` |
| P0-5 thiếu ownership + seed quyền | `EnsureOwner` trong create/refund/retry; seed từ toàn bộ `PermissionsCatalog`; Rider có `payments.read`; chặn tự đăng ký Admin | `PaymentAppServiceTests.CreatePayment_OtherRider_Forbidden`, `RbacRegressionTests` |
| P0-6 outbox dispatcher không transaction-safe | Bọc trọn chu kỳ select-lock → publish → đánh dấu → commit trong MỘT transaction (cả PaymentService và TripService) | `OutboxDispatcherWorker.cs`, `TripService/Infrastructure/Messaging/OutboxDispatcherWorker.cs` |
