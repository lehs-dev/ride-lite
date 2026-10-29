# Tiến độ hoàn thành TripService

> Ngày lập: 26/10/2026. Mốc mã nguồn khảo sát: `b04118c` và working tree hiện tại.
> Phạm vi: TripService, Trip API, event publishing/consuming liên quan đến chuyến đi, tests và bug fix.
> Đây là kế hoạch triển khai và nghiệm thu; các checkbox chưa đánh dấu là công việc chưa hoàn thành.

## 1. Cách dùng tài liệu

- Làm theo thứ tự M0 đến M7. Viết test cùng từng chức năng, không chờ đến M6 mới bắt đầu test.
- Mỗi checkbox có mã công việc để ghi vào commit, PR hoặc nhật ký ở cuối file.
- Chỉ đánh dấu hoàn thành khi có code, kết quả kiểm thử và bằng chứng đáp ứng tiêu chí của mốc.
- Trạng thái dùng trong bảng: `Chưa làm`, `Đang làm`, `Chờ phối hợp`, `Chờ nghiệm thu`, `Hoàn thành`.
- Những tên bảng, endpoint bổ sung, trường event và thời lượng ghi là **đề xuất** chưa phải hợp đồng đã được nhóm chấp thuận.
- Không suy ra tiến độ từ việc đã có thư mục, đã cài package hoặc một test rỗng chạy thành công.

### Nguồn đối chiếu

| Nguồn | Dùng để xác định |
|---|---|
| [Tài liệu môn học](../RideLite-KienTrucPhanMem.pdf) | Phạm vi nghiệp vụ, state machine, SOLID, patterns và yêu cầu chất lượng |
| [EventContract.md](EventContract.md) | Đề xuất phối hợp Trip - Payment - Messaging; hiện **chưa chốt** |
| [Program.cs của Trip](../src/TripService/Program.cs) | Cấu hình khởi động và hai Trip endpoint đang trả 501 |
| [TripDbContext.cs](../src/TripService/Infrastructure/Persistence/TripDbContext.cs) và [TripDbSeeder.cs](../src/TripService/Infrastructure/Persistence/TripDbSeeder.cs) | Mô hình RBAC và cơ chế tạo schema hiện tại |
| [AuthService.cs](../src/TripService/Application/Services/AuthService.cs) và [AuthEndpoints.cs](../src/TripService/Api/AuthEndpoints.cs) | Hành vi xác thực, đăng ký và phân quyền hiện có |
| [AdminEndpoints.cs](../src/TripService/Api/AdminEndpoints.cs) | Quản lý user và danh sách chuyến đang dùng dữ liệu mẫu |
| [Security](../src/Security/AuthenticationExtensions.cs) và [PermissionsCatalog.cs](../src/Security/PermissionsCatalog.cs) | JWT, fallback policy và danh mục permission |
| [Gateway config](../src/ApiGateway/appsettings.json) và [Gateway pipeline](../src/ApiGateway/Program.cs) | Routing, authorization, CORS và rate limit |
| [compose.yaml](../.devcontainer/compose.yaml) | Hạ tầng database, broker, collector và workspace |
| [TripService.Tests](../tests/TripService.Tests/UnitTest1.cs) và [CI](../.github/workflows/ci.yml) | Mức kiểm thử và cách build/test hiện có |

Số trang được dẫn bên dưới là **trang trong PDF, tính cả bìa**, không phải số trang in ở chân trang. Khi tài liệu nhóm và PDF khác nhau, giữ yêu cầu nghiệp vụ của PDF; ghi lại quyết định triển khai của nhóm. Không coi nội dung đề xuất trong EventContract là quyết định đã được xác nhận.

## 2. Hiện trạng đã xác minh

| Hạng mục | Kết quả tại thời điểm lập kế hoạch | Phần cần làm tiếp |
|---|---|---|
| Framework | .NET 10; SDK máy khảo sát là 10.0.100 | Giữ phiên bản nhất quán trong nhóm/CI |
| Cấu trúc TripService | Có Domain/Application/Infrastructure/Api trong một project | Bổ sung nghiệp vụ Trip; hướng phụ thuộc cần được thể hiện bằng abstraction |
| Auth/RBAC | Có năm entity và mapping; login/register/me, quản lý user/role/permission | Sửa các lỗi ở mục 10; giữ hoạt động khi thêm Trips |
| Database | DbContext chỉ có bảng RBAC; seeder gọi EnsureCreatedAsync | Chưa có Trip/TripState/migrations; phải kiểm kê DB thực tế trước khi chuyển đổi |
| Trip API | POST /trips và POST /trips/{id}/driver-accept trả 501 | Viết handler thật và các API còn thiếu |
| Admin trips | Trả dữ liệu tạo sẵn trong code | Đọc Trips thật, có phân trang |
| Matching/Payment | Các endpoint nghiệp vụ trong hai service cũng đang là scaffold | Là phụ thuộc tích hợp; dùng fake trong test nhưng nghiệm thu E2E phải dùng service thật |
| Contracts | Project dùng chung chưa có Event DTO | Phối hợp người phụ trách contract, tránh tạo DTO khác namespace/type |
| Messaging | Trip đã tham chiếu MassTransit.RabbitMQ nhưng chưa đăng ký bus, publisher hoặc consumer | Triển khai tại M5 |
| Docker Compose | Có workspace, SQL Server, MongoDB, PostgreSQL, RabbitMQ và OTel | Chưa khai báo container gateway/trip-service/matching-service/payment-service |
| Docker runtime | Lệnh docker ps theo label project rite-lite-dev không trả container đang chạy | Chưa xác nhận DB/schema/dữ liệu thực tế; không kết luận toàn máy không có container |
| Build Release | Thành công, 0 error, 8 warning | Các warning hiện liên quan NU1902/NU1903 của dependency Pkcs 9.0.11 |
| Test Release | 8 Security test + 1 TripService test pass | Test TripService là hàm rỗng, chưa chứng minh nghiệp vụ |
| Gateway anonymous smoke | Cả /auth/login, /auth/register, /admin/auth/login trả 401 khi POST body {} không token | Sửa fallback authorization cho route công khai; smoke này không kiểm tra credentials hay database |

Lệnh baseline đã chạy:

```powershell
dotnet build SwiftRide.sln -c Release --nologo --verbosity minimal
dotnet test SwiftRide.sln -c Release --no-build --nologo --verbosity minimal
docker ps --filter label=com.docker.compose.project=rite-lite-dev
```

Gateway smoke dùng một tiến trình riêng ở `127.0.0.1:15990`, đọc config hiện tại và đã dừng sau kiểm tra. Chưa chạy E2E với SQL Server/RabbitMQ, chưa chạy migration, chưa sửa bug ứng dụng.

## 3. Đối chiếu yêu cầu môn học với sản phẩm cần có

| Mã | Yêu cầu | Nguồn PDF | Bằng chứng nghiệm thu | Mốc |
|---|---|---|---|---|
| R01 | Tối đa 3 service nghiệp vụ, có Payment; mỗi service sở hữu dữ liệu | Mục 1, 2.4, 3; trang 3-5 | Không tạo thêm Identity service; Trip không đọc DB Payment/Matching | M0, M7 |
| R02 | Tạo và lưu chuyến Requested | Mục 2.2, 5.1; trang 3, 7 | POST /trips tạo bản ghi có RiderId từ token | M1-M3 |
| R03 | Matching/báo giá; rider xác nhận hoặc từ chối giá | Mục 2.2, 4; trang 3, 6 | Hai nhánh xác nhận/hủy chạy được; không nhận giá tùy ý từ client | M3-M4 |
| R04 | Tài xế từ chối thì tìm lại, quá giới hạn k thì thất bại | Mục 2.2, 4; trang 3, 6 | Test trường hợp ở ngưỡng và vượt ngưỡng; demo Failed | M2, M4 |
| R05 | Nhận chuyến, đang đến, đón khách, trả khách | Mục 4, 5.1, 7; trang 6, 7, 9 | State pattern và API tương ứng; sai thứ tự bị từ chối | M2-M3 |
| R06 | Trả khách kích hoạt thanh toán; chỉ đóng thành công khi đã thanh toán | Mục 2.2, 4; trang 3, 6 | RideCompleted tới Payment; PaymentSettled đưa Trip sang Paid | M5, M7 |
| R07 | Thanh toán lỗi/chờ xử lý, retry giới hạn và không trừ tiền lặp | Mục 4, 5.3, 9.2; trang 6, 7, 11 | Duplicate/retry không tạo nghiệp vụ lặp; không báo Paid sai | M5-M7, phối hợp Payment |
| R08 | State, Repository và áp dụng SOLID | Mục 5.1, 6, 7; trang 7-10 | Domain giữ quy tắc; application phụ thuộc interface; endpoint mỏng | M1-M3 |
| R09 | Client qua Gateway, service vẫn kiểm tra tài nguyên | Mục 3, 9.3; trang 4-5, 11 | Test qua Gateway và ownership trực tiếp tại Trip | M3, M6 |
| R10 | Lời gọi mạng có timeout; retry lỗi tạm thời | Mục 9.2; trang 11 | Test timeout, lỗi nghiệp vụ không retry, cấu hình hữu hạn | M4-M5 |
| R11 | Secret ngoài source; correlation/trace; không log dữ liệu nhạy cảm | Mục 9.3; trang 11 | Config qua môi trường; kiểm tra log; trace qua luồng Trip/Payment | M0, M5-M7 |
| R12 | Có thể chạy luồng đơn giản trước; broker/Outbox/observability là mở rộng | Mục 1, 9.1; trang 3, 11 | Không đánh đồng mọi pattern trong tài liệu với một tính năng phải tự viết | Toàn kế hoạch |

PDF cho phép cùng loại CSDL nếu tách database/schema theo service (trang 5). Repo đã chọn SQL Server cho Trip nên tiếp tục lựa chọn này. Không chuyển DB chỉ để đáp ứng cách diễn đạt ở phần tóm tắt.

**Phạm vi người dùng đã yêu cầu thêm:** event publishing thuộc đầu ra của phần Trip, nên M5 phải có giao tiếp event thật dù broker được PDF xem là phần mở rộng. Kế hoạch chọn Outbox/Inbox để kiểm soát mất/trùng sự kiện; đây là lựa chọn triển khai về độ tin cậy, không phải khẳng định PDF bắt buộc mọi nhóm dùng Outbox. Nếu nhóm giảm phạm vi này, phải sửa tiêu chí nghiệm thu và ghi rõ rủi ro save DB thành công nhưng publish thất bại.

Không đưa tính giá Strategy, PaymentMethod Factory, sổ cái, hoàn tiền hoặc Saga toàn hệ thống vào phần phải tự triển khai của Trip. Các phần này cần phối hợp đúng service sở hữu.

## 4. Bảng tiến độ tổng

Ước lượng cho một người đã quen C#/.NET, gồm code và kiểm thử; chưa gồm thời gian chờ nhóm hoặc học công nghệ. Tổng dự kiến **49-78 giờ**, là dự toán để chia việc, không phải deadline cam kết.

| Mốc | Đầu ra | Phụ thuộc | Giờ dự kiến | Trọng số | Trạng thái |
|---|---|---|---|---|---|
| M0 | Chốt phạm vi/contract, sửa chặn đăng nhập, chuẩn bị môi trường | Không | 3-5 | 5% | Chưa làm |
| M1 | Mô hình dữ liệu và migrations an toàn | M0, khung entity ở M2 | 6-10 | 15% | Chưa làm |
| M2 | Domain Trip và State pattern | Quyết định M0; có thể làm trước mapping M1 | 6-10 | 15% | Chưa làm |
| M3 | Application và Trip API thật | M1, M2 | 10-16 | 20% | Chưa làm |
| M4 | Matching/Pricing và nhánh tìm lại | M3, contract Matching | 5-8 | 10% | Chưa làm |
| M5 | Event publishing, kết quả Payment và độ tin cậy | M3, contract Payment/F | 8-12 | 15% | Chưa làm |
| M6 | Regression, integration, security và bug fix | Test đi cùng M1-M5; tổng duyệt sau M5 | 8-12 | 15% | Chưa làm |
| M7 | Demo E2E, tài liệu và bàn giao | M0-M6 | 3-5 | 5% | Chưa làm |

Thứ tự thực tế: **M0 -> khung domain M2 -> mapping/migrations M1 -> hoàn tất M2 -> M3 -> M4 -> M5 -> tổng duyệt M6 -> M7**. Không tạo migration trước khi có entity/mapping. Sau khi M3 ổn định có thể tiến hành M4 hoặc M5 theo mức sẵn sàng của các service khác.

Tiến độ có trọng số = tổng trọng số các mốc đã nghiệm thu. Nếu cần theo dõi trong mốc, tính thêm tỷ lệ checkbox đã đạt trong mốc đó, ghi rõ chỉ là ước lượng. Tại lúc lập file, **chưa có mốc mới nào được nghiệm thu**; không quy đổi code Auth hiện có thành phần trăm hoàn thành nghiệp vụ Trip.

## 5. Quyết định cần chốt tại M0

| Mã | Vấn đề | Đề xuất ban đầu | Bên cần phối hợp | Trạng thái |
|---|---|---|---|---|
| D01 | TripId và định danh actor | Guid; RiderId/DriverId khớp sub trong JWT | Trip, Matching, Payment | Chưa chốt |
| D02 | Số tiền/currency | decimal; VND; dùng số tiền rider đã xác nhận, không tự cộng phí sau chuyến trong bản cơ bản | Trip, Pricing, Payment | Chưa chốt |
| D03 | Quote hết hạn/thay đổi khi rematch | Lưu QuoteId, expiresAt; giá đổi thì yêu cầu rider xác nhận lại | Trip, Matching | Chưa chốt |
| D04 | Giới hạn tìm lại | k là số lần được rematch sau từ chối; tăng RejectionCount trước khi so sánh | Trip, Matching | Chưa chốt |
| D05 | Trước khi driver nhận | Giữ Requested, dùng BookingPhase và offer để phân biệt chờ quote/rider/driver | Trip, frontend, Matching | Chưa chốt |
| D06 | Hủy chuyến | Bản cơ bản cho rider hủy trước PickedUp; chưa có phí hủy | Trip, frontend | Chưa chốt |
| D07 | Event payload và namespace | Dùng chung DTO trong Contracts; bổ sung trường theo mục 9 | Trip (B), Payment (E), Messaging (F) | Chưa chốt |
| D08 | PaymentFailed/recovery | Giữ PaymentPending + trạng thái phụ cần xử lý; retry/reconcile dùng cùng payment key | Trip, Payment, F | Chưa chốt |
| D09 | Outbox/Inbox | Chọn một cơ chế dùng chung với F, ưu tiên tận dụng khả năng phù hợp của MassTransit/EF | Trip, F | Chưa chốt |
| D10 | Tài khoản bị khóa/quyền bị thu hồi | Chốt mức hiệu lực tức thì hay sau token expiry; thực thi và test đúng chính sách | Trip, Security | Chưa chốt |
| D11 | Auth theo đề | Đối chiếu JWT hiện tại với OAuth2/OIDC nêu trong PDF; ghi rõ nếu nhóm dùng JWT local cho phạm vi demo | Nhóm, giảng viên nếu cần | Chưa chốt |

Theo EventContract hiện có, B/E/F là ký hiệu vai trò phối hợp; không tự giao toàn bộ phần việc của F hoặc E cho người làm Trip. Không tự sửa contract chung thành quyết định cuối cùng khi chưa có thống nhất.

## 6. Checklist triển khai theo mốc

### M0 - Chuẩn bị và bỏ các điểm chặn

- [ ] **M0-01** Ghi quyết định D01-D11 vào tài liệu nhóm; điểm chưa chốt có người phụ trách và cách tiếp tục bằng fake/test.
- [ ] **M0-02** Chuẩn bị biến môi trường từ .env.example hoặc user-secrets; không đưa mật khẩu/JWT key thật vào tài liệu, commit hay log. Xử lý BUG-05.
- [ ] **M0-03** Chọn cách chạy: host Windows hoặc devcontainer; cấu hình địa chỉ downstream đúng mạng. Xử lý BUG-03.
- [ ] **M0-04** Khởi động và kiểm tra SQL Server/RabbitMQ; xác nhận kết nối từ chính tiến trình Trip. Không suy ra DB sẵn sàng từ việc container đã started.
- [ ] **M0-05** Sửa route public ở Gateway theo BUG-02, test login/register/admin-login không bị Gateway yêu cầu token trước.
- [ ] **M0-06** Chặn tự đăng ký Admin theo BUG-01, bổ sung regression test trước khi dùng API public để demo.
- [ ] **M0-07** Thống nhất F tạo/review Event DTO và cấu hình broker; E cung cấp event kết quả; Matching cung cấp request/response mẫu.

**Nghiệm thu:** đăng nhập Rider/Driver qua Gateway với DB thật được; đăng ký không cấp Admin; có danh sách quyết định đã chốt và phụ thuộc còn chờ. Khi chưa có service khác, unit/API test chạy bằng fake được nhưng không đánh dấu E2E hoàn thành.

### M1 - Database và persistence

- [ ] **M1-01** Kiểm kê TripDB thực tế: bảng, dữ liệu cần giữ, schema và có/không __EFMigrationsHistory; sao lưu dữ liệu cần giữ trước thay đổi schema.
- [ ] **M1-02** Tạo khung Trip/TripStatus/BookingPhase ở M2 trước; sau đó thống nhất mapping theo mục 7.
- [ ] **M1-03** Chuyển từ EnsureCreated sang migrations theo tình trạng DB; không chạy initial migration tạo lại bảng RBAC đang tồn tại. Chọn baseline được kiểm tra hoặc DB dev mới có tên riêng.
- [ ] **M1-04** Tách seed khỏi tạo schema; seed lặp lại không tạo bản ghi trùng và không âm thầm khôi phục permission quản trị đã chủ động thu hồi.
- [ ] **M1-05** Bổ sung Trips, lịch sử trạng thái, quote/offer cần thiết; mapping tiền, tọa độ, thời gian UTC, chỉ mục và concurrency token.
- [ ] **M1-06** Tạo abstraction ITripReader/ITripWriter hoặc repository tương đương; thao tác nghiệp vụ có đơn vị transaction rõ ràng.
- [ ] **M1-07** Bảo đảm lưu Trip + history + outgoing event của một thao tác là nguyên tử khi bật Outbox; không SaveChanges rời rạc từng phần.
- [ ] **M1-08** Kiểm tra migration trên DB trắng và đường nâng cấp từ schema RBAC cũ bằng SQL Server thật; xác nhận user/role hiện có vẫn dùng được.
- [ ] **M1-09** Sửa hành vi startup/readiness theo BUG-09; lỗi schema/config không bị che bằng thông báo sẽ tự seed lại dù không có retry.

**Nghiệm thu:** có migration/script review được, tạo schema mới thành công, nâng cấp không mất dữ liệu cần giữ; hai DbContext cập nhật đồng thời được kiểm soát; Auth/RBAC không hỏng.

### M2 - Domain Trip và State pattern

- [ ] **M2-01** Tạo Trip aggregate/entity với phương thức nghiệp vụ; không cho endpoint gán Status tùy ý.
- [ ] **M2-02** Triển khai State pattern cho các chuyển trạng thái ở mục 7; chỉ có enum + switch rải khắp endpoint chưa thể hiện đầy đủ mục tiêu pattern của đề.
- [ ] **M2-03** Giữ Requested nhưng phân biệt các phase trước nhận chuyến; xác nhận giá phải gắn đúng quote và phiên bản hiện tại.
- [ ] **M2-04** Quy định driver accept/reject chỉ được thực hiện bởi tài xế của offer còn hiệu lực; decline cũ không làm mất driver mới.
- [ ] **M2-05** Quy định ai được hủy và đến mốc nào; không cho Cancelled/Failed/Paid quay lại luồng đang chạy bằng API thông thường.
- [ ] **M2-06** Đóng băng giá đã xác nhận; dropoff phát sinh domain event đúng một lần về mặt nghiệp vụ, chưa tự đánh dấu Paid.
- [ ] **M2-07** Phân biệt domain event nội bộ và integration event DTO; domain không phụ thuộc MassTransit, HttpClient hoặc EF Core.
- [ ] **M2-08** Viết unit test cho đường hợp lệ, mọi chuyển trạng thái bị cấm, xác nhận giá, giới hạn k và lặp thao tác.

**Nghiệm thu:** có thể chạy toàn bộ vòng đời bằng domain test không cần DB/broker; có giải thích State/SRP/ISP/DIP bằng code cụ thể cho báo cáo môn học.

### M3 - Application và Trip API

- [ ] **M3-01** Tạo DTO cho từng thao tác, TripResponse và lỗi; RiderId/acting DriverId lấy từ claims, không tin dữ liệu actor trong body.
- [ ] **M3-02** Viết các use case create/read/list/confirm/accept/reject/en-route/pickup/dropoff/cancel qua abstraction; truyền CancellationToken.
- [ ] **M3-03** Tạo TripEndpoints.cs; thay hai route trả 501 trong Program.cs, không để route trùng và không chuyển business logic vào Program.cs.
- [ ] **M3-04** Ánh xạ route, permission và response theo mục 8; bổ sung permission mới chỉ khi thực sự cần, cập nhật cả catalog lẫn seed.
- [ ] **M3-05** Kiểm tra ownership: rider của chuyến, driver được mời/đã nhận; admin có quyền đọc quản trị nhưng không tự động bỏ qua mọi điều kiện nghiệp vụ.
- [ ] **M3-06** Validate chuỗi địa chỉ, tọa độ, quote, thời hạn, kích thước request và phân trang; lỗi validation không thành 500.
- [ ] **M3-07** Dùng concurrency token/conditional update để nhận chuyến, hủy và cập nhật trạng thái không ghi đè lẫn nhau; chuyển xung đột thành 409.
- [ ] **M3-08** Chốt idempotency của create và các lệnh quan trọng: cùng key + cùng payload trả kết quả cũ; cùng key + payload khác trả 409; ràng buộc theo actor/operation.
- [ ] **M3-09** Thay mock admin trips bằng query thật, phân trang; không lộ chuyến của người khác qua GET/list.
- [ ] **M3-10** Tạo API integration tests bằng host test và dependency có thể thay thế; kiểm tra 400/401/403/404/409 cùng response thành công.

**Nghiệm thu:** các API trong phạm vi không còn 501; restart không mất Trip; không vượt ownership bằng cách thay id trong URL/body; thao tác đồng thời không tạo hai kết quả thành công mâu thuẫn.

### M4 - Tích hợp Matching/Pricing và nhánh tìm lại

- [ ] **M4-01** Chốt contract POST /quote và POST /match: đơn vị distance/time, amount/currency, QuoteId, expiry, DriverId và offer/candidate id.
- [ ] **M4-02** Tạo IMatchingClient và HTTP implementation qua cấu hình/DI; test dùng fake. Không truy vấn MongoDB từ Trip.
- [ ] **M4-03** Lưu Requested trước; gọi dịch vụ ngoài ngoài transaction DB dài; cập nhật kết quả có kiểm tra version/phase để response đến muộn không hồi sinh chuyến đã hủy.
- [ ] **M4-04** Lưu quote/candidate; chỉ gửi offer sau khi rider xác nhận; không cho driver nhận khi rider chưa đồng ý giá.
- [ ] **M4-05** Driver reject tăng bộ đếm, loại driver đã từ chối và tạo offer mới đúng giới hạn k; offer cũ vô hiệu. Có nhánh no-driver và quote-expired rõ ràng.
- [ ] **M4-06** Cấu hình timeout/retry hữu hạn cho lỗi tạm thời; không retry lỗi nghiệp vụ/validation; chỉ tự retry lời gọi có ngữ nghĩa an toàn hoặc idempotency đã chốt.
- [ ] **M4-07** Kiểm tra real Matching và các ca timeout/no-driver/quote thay đổi; request giả trong test không được dùng làm bằng chứng tích hợp thật.

**Nghiệm thu:** rider đồng ý/từ chối giá chạy được; reject đến ngưỡng và vượt ngưỡng đúng quy tắc; response trễ không ghi đè trạng thái mới; lỗi Matching không để HTTP treo vô hạn.

### M5 - Event publishing và nhận kết quả Payment

- [ ] **M5-01** Chốt D07-D09 với E/F; dùng đúng contract assembly/namespace/version chung. Ví dụ JSON trong EventContract chưa đủ để viết consumer chính thức.
- [ ] **M5-02** Tích hợp cấu hình MassTransit/RabbitMQ cùng F: host/credential theo môi trường, endpoint nhận kết quả Payment riêng cho Trip, consumer đăng ký rõ ràng.
- [ ] **M5-03** Thiết kế một publisher abstraction nếu cần cho application; dùng lifetime phù hợp với cơ chế publish/transaction đã chọn, không copy singleton event bus minh họa một cách máy móc.
- [ ] **M5-04** Khi dropoff hợp lệ, lưu history DroppedOff, chuyển PaymentPending và ghi intent RideCompleted trong cùng transaction; Amount/Currency lấy từ dữ liệu server đã chốt.
- [ ] **M5-05** Chọn và triển khai một Outbox bền vững; dispatcher publish lại sau lỗi/restart, giữ nguyên EventId/payment key. Không chỉ dùng in-memory outbox để tuyên bố chống mất event khi process chết.
- [ ] **M5-06** Consumer PaymentSettled kiểm tra TripId, payment identity, amount/currency theo contract; chỉ chuyển PaymentPending sang Paid, lưu transaction/payment reference và history.
- [ ] **M5-07** Consumer có Inbox/dedup bền vững; ghi dấu đã xử lý cùng transaction cập nhật Trip. Hai consumer xử lý cùng event đồng thời vẫn chỉ tạo một hiệu ứng nghiệp vụ.
- [ ] **M5-08** Xử lý PaymentFailed theo contract: giữ chuyến cần xử lý, không đánh dấu Paid hoặc tự động hoàn tác việc đã chở khách; sự kiện failure đến sau success không hạ trạng thái Paid.
- [ ] **M5-09** Thống nhất retry/redelivery/error queue với F; lỗi dữ liệu vĩnh viễn không retry vô hạn; phân biệt retry kỹ thuật với phát sinh yêu cầu thanh toán nghiệp vụ mới.
- [ ] **M5-10** Truyền correlation/causation/trace context; log định danh Trip/Event vừa đủ, không log token hoặc payload nhạy cảm.
- [ ] **M5-11** Test bằng messaging harness cho publish/consume, sau đó RabbitMQ thật cho routing/serialization/retry; test SQL Server thật cho transaction Outbox/Inbox.
- [ ] **M5-12** Phối hợp E chứng minh RideCompleted trùng không trừ tiền lần hai; Trip không thể tự bảo đảm việc này chỉ bằng test publisher.

**Nghiệm thu:** dropoff cuối cùng dẫn đến đúng một thanh toán nghiệp vụ; PaymentSettled đưa Trip thành Paid; event trùng/out-of-order không phá trạng thái; broker ngắt rồi phục hồi không mất intent đã commit. Việc broker có thể giao message nhiều lần là bình thường, không đặt tiêu chí "broker chỉ giao đúng một lần".

TripAccepted có trong PDF và có thể bổ sung với consumer/nhu cầu rõ ràng sau hai event chính. TripCancelled hoặc các event khác là mở rộng cần thống nhất, không tự thêm vào contract của người khác.

### M6 - Tests, regression và bug fix

- [ ] **M6-01** Thay test rỗng bằng các nhóm test mục 11; giữ các Security test đang có.
- [ ] **M6-02** Thêm dependency test đúng nhu cầu, như API host test/messaging harness; version tương thích stack hiện có. Không dùng EF InMemory để kết luận rowversion/unique constraint/transaction SQL Server đúng.
- [ ] **M6-03** Hoàn tất regression cho BUG-01 đến BUG-10; mỗi lỗi có bước tái hiện, nguyên nhân, fix và test; điểm chính sách chưa chốt phải được ghi rõ.
- [ ] **M6-04** Kiểm thử đồng thời bằng hai scope/DbContext và request thật: accept/accept, accept/cancel, duplicate dropoff và duplicate payment event.
- [ ] **M6-05** Kiểm thử auth/permission/ownership cả qua Gateway lẫn trực tiếp service; không dùng token Admin để thay cho test Rider/Driver.
- [ ] **M6-06** Audit dependency theo BUG-11, xác định đường phụ thuộc, chọn bản sửa tương thích và chạy lại test; không tắt audit để làm mất warning.
- [ ] **M6-07** Bổ sung CI integration job cùng F nếu cần SQL Server/RabbitMQ; phân biệt unit job với integration job, không để test bỏ qua im lặng vì thiếu hạ tầng.
- [ ] **M6-08** Chạy build/test Release và smoke E2E; ghi kết quả thực tế, lỗi còn lại, không lấy số test pass làm thay thế chất lượng case.

**Nghiệm thu:** test matrix có bằng chứng cho nhóm bắt buộc; không còn lỗi chặn đăng nhập/tự cấp quyền/mất hoặc thu tiền trùng/sai state; lỗi hoãn có tác động và lý do rõ ràng.

### M7 - Demo và bàn giao

- [ ] **M7-01** Thực hiện đủ kịch bản demo ở mục 12 với DB và broker thật; dữ liệu test tách khỏi dữ liệu cần giữ.
- [ ] **M7-02** Cập nhật API flow, schema, state diagram, event contract đã thống nhất và hướng dẫn chạy từ checkout mới.
- [ ] **M7-03** Chuẩn bị collection .http/Postman hoặc script có thể chạy lại, lấy token runtime; không commit token còn hiệu lực hoặc mật khẩu thật.
- [ ] **M7-04** Viết phần báo cáo trách nhiệm Trip, SOLID, State/Repository/Pub-Sub và trade-off consistency/Outbox, nêu rõ phần nhóm khác sở hữu.
- [ ] **M7-05** Tổng duyệt Definition of Done ở mục 13; điền bảng tiến độ, bằng chứng và danh sách việc hoãn.

**Nghiệm thu:** thành viên khác làm theo hướng dẫn chạy được luồng đầy đủ; trình bày được cả happy path lẫn nhánh lỗi mà không sửa DB thủ công để giả lập thành công.

## 7. Khung dữ liệu và trạng thái đề xuất

### 7.1 Dữ liệu cần lưu

| Thành phần | Trường/đặc điểm đề xuất | Quy tắc |
|---|---|---|
| Trips: định danh | Id, RiderId, DriverId nullable | Guid; driver chỉ gán khi accept hợp lệ; candidate lưu riêng trong offer |
| Trips: hành trình | PickupAddress, PickupLatitude/Longitude, DestinationAddress, DestinationLatitude/Longitude | Validate miền tọa độ, độ dài và dữ liệu bắt buộc |
| Trips: quote | QuoteId, QuoteExpiresAtUtc, QuotedAmount, AcceptedAmount, Currency, FareAcceptedAtUtc | decimal có precision rõ ràng, không dùng float cho tiền; giá server quản lý |
| Trips: vòng đời | Status, BookingPhase, CreatedAtUtc, UpdatedAtUtc và các mốc nghiệp vụ | UTC; chỉ domain chuyển trạng thái |
| Trips: tìm lại | RejectionCount, cấu hình MaxRematchRetries | Phân biệt retry HTTP với tài xế từ chối nghiệp vụ |
| Trips: thanh toán | PaymentRequestKey, PaymentId/TransactionId, PaymentResolutionStatus | Key ổn định cho một nghĩa vụ thanh toán; không lưu thẻ/CVV |
| Trips: đồng thời | RowVersion | SQL Server concurrency token; có test cạnh tranh thật |
| TripStatusHistories | Id, TripId, FromStatus, ToStatus, ActorId/System, Reason, OccurredAtUtc, EventId nullable | Append lịch sử cùng transaction; không chứa token/PII không cần thiết |
| TripDriverOffers | Id, TripId, DriverId, AttemptNumber, Status, ExpiresAtUtc | Xác định offer hiện hành và loại driver đã từ chối; không tin DriverId do client tự gửi |
| Request idempotency | ActorId, Operation, Key, RequestHash, TripId/Result | Unique theo phạm vi đã chốt; có chiến lược giữ/xóa và giải quyết request đang xử lý |
| Outbox/Inbox | Theo cơ chế thống nhất với F | Có unique key, retry và transaction phù hợp; không tự tạo thêm bảng trùng với schema thư viện |

Chỉ mục dự kiến: RiderId + CreatedAtUtc; DriverId + Status; TripId + thời gian cho history; khóa duy nhất cho request key, event dedup và offer/attempt. Quyết định FK vào Users trong TripDB theo mô hình actor thực tế; không tạo FK chéo sang database của service khác. Không cascade xóa tài khoản làm mất lịch sử chuyến.

### 7.2 Bảng chuyển trạng thái

| Trạng thái/phase đầu | Hành động/điều kiện | Kết quả | Chủ thể |
|---|---|---|---|
| Chưa có | Tạo yêu cầu hợp lệ | Requested / AwaitingQuote | Rider từ token |
| Requested / AwaitingQuote | Nhận quote hợp lệ từ Matching | Requested / AwaitingRiderConfirmation | Application qua client |
| Requested / AwaitingRiderConfirmation | Đồng ý đúng quote còn hạn | Requested / AwaitingDriverResponse, mở offer | Rider sở hữu |
| Requested / AwaitingRiderConfirmation | Từ chối giá | Cancelled | Rider sở hữu |
| Requested / AwaitingDriverResponse | Offer hiện hành được nhận | DriverAccepted | Driver được mời |
| Requested / AwaitingDriverResponse | Driver từ chối, chưa vượt k | Giữ Requested, tìm candidate/offer khác | Driver được mời + application |
| Requested / AwaitingDriverResponse | Vượt k hoặc không còn candidate theo chính sách | Failed | Application |
| DriverAccepted | Bắt đầu đến điểm đón | EnRoute | Driver đã nhận |
| EnRoute | Xác nhận đón khách | PickedUp | Driver đã nhận |
| PickedUp | Trả khách | DroppedOff rồi PaymentPending; ghi history và outgoing event | Driver đã nhận |
| PaymentPending | PaymentSettled hợp lệ | Paid | Consumer nội bộ |
| PaymentPending | PaymentFailed cuối cùng hoặc cần đối soát | Giữ PaymentPending, đánh dấu cần xử lý | Consumer/application theo contract |
| Requested/DriverAccepted/EnRoute | Hủy trong phạm vi D06 | Cancelled | Rider sở hữu; chính sách admin nếu có phải nêu riêng |
| Paid/Cancelled/Failed | Lệnh không phù hợp trạng thái cuối | Từ chối; không thay đổi dữ liệu | Mọi actor |

DroppedOff và PaymentPending có thể là hai bước domain/history trong một transaction dropoff. Không để khoảng trống mà PaymentSettled đến trước khi dữ liệu sẵn sàng nhận kết quả. BookingPhase là đề xuất bổ sung, không phải thuật ngữ được PDF quy định.

Ví dụ chốt giới hạn: nếu `k = 2` là **số lần rematch**, lần từ chối thứ 1 và 2 còn được tìm lại; lần thứ 3 làm chuyến Failed. Nếu nhóm muốn k là tổng số offer thì phải đổi tên/công thức và test tương ứng, không trộn hai cách hiểu.

## 8. Trip API dự kiến

Các route bổ sung dưới đây là thiết kế đề xuất để hiện thực flowchart; PDF chỉ nêu một số API tiêu biểu. Response DTO thống nhất có TripId, Status/phase, giá đã chốt và timestamps cần cho client; không trả entity EF trực tiếp.

| Method/route | Actor và permission dự kiến | Kết quả thành công | Điều kiện quan trọng |
|---|---|---|---|
| POST /trips | Rider; trips.create | 201 + Location + TripResponse | Lưu Requested; RiderId từ token; hỗ trợ request key theo D đã chốt |
| GET /trips/{id} | Chủ chuyến/driver liên quan; trips.read | 200 | Permission không thay thế ownership |
| GET /trips | Rider/Driver; trips.read | 200 + page | Tự lọc theo actor; driver chỉ thấy offer/chuyến hợp lệ của mình |
| POST /trips/{id}/confirm-fare | Rider sở hữu; trips.create | 200 | Body nhận QuoteId/decision cần thiết, không cho tự nhập giá |
| POST /trips/{id}/driver-accept | Driver được mời; trips.accept | 200 | Offer còn hạn; giá đã được xác nhận |
| POST /trips/{id}/driver-reject | Driver được mời; trips.accept | 200 | Tăng bộ đếm và rematch/Failed; từ chối lặp không tăng bộ đếm lần hai |
| POST /trips/{id}/en-route | Driver đã nhận; trips.update_status | 200 | Chỉ từ DriverAccepted |
| POST /trips/{id}/pickup | Driver đã nhận; trips.update_status | 200 | Chỉ từ EnRoute |
| POST /trips/{id}/dropoff | Driver đã nhận; trips.update_status | 200, PaymentPending | Commit dữ liệu + outgoing event; chưa hứa thanh toán xong |
| POST /trips/{id}/cancel | Rider sở hữu; trips.cancel | 200 | Theo D06; không hủy chuyến đã chở xong để né thanh toán |
| GET /admin/trips | Admin theo Gateway và quyền Trip phù hợp | 200 + page | Query DB thật, thay mock; không cấp API admin cho Rider chỉ vì có trips.read |

PaymentSettled/PaymentFailed đi qua consumer, **không tạo API public cho client tự đánh dấu Paid**. Nếu cần thao tác đối soát/admin retry thì phải có policy, audit và contract riêng; chưa nằm trong API cơ bản.

Quy ước lỗi: 400 cho validation, 401 cho thiếu/sai token, 403 cho thiếu quyền/ownership, 404 cho tài nguyên không tồn tại, 409 cho xung đột trạng thái/concurrency/idempotency. Chốt cách che tài nguyên trái quyền nếu nhóm chọn trả 404. Lỗi upstream trả mã/ProblemDetails đã thống nhất và trạng thái recoverable; không biến mọi lỗi thành 500 hoặc Failed vĩnh viễn. Lệnh lặp cùng actor và cùng tác động đã hoàn tất có thể trả kết quả cũ theo quy tắc idempotency, nhưng không phát sinh event/history lần hai.

## 9. Checklist contract và publishing chi tiết

### 9.1 Các điểm cần bổ sung vào bản thống nhất

| Event | Chiều | Nội dung cần chốt | Hiệu ứng tại Trip |
|---|---|---|---|
| RideCompleted | Trip -> Payment | EventId, TripId, RiderId, DriverId nếu cần, Amount, Currency, OccurredAtUtc, CorrelationId, PaymentRequestKey; quy ước version/causation | Ghi intent sau dropoff; giữ PaymentPending |
| PaymentSettled | Payment -> Trip | EventId, TripId, PaymentId/TransactionId, PaymentRequestKey, Amount, Currency, OccurredAtUtc, CorrelationId | Xác minh đúng nghĩa vụ thanh toán rồi chuyển Paid |
| PaymentFailed | Payment -> Trip | EventId, TripId, PaymentRequestKey, mã lý do, đã hết retry hay còn retry, thời điểm, correlation | Giữ trạng thái cần xử lý; không hạ Paid nếu sự kiện đến muộn |

EventContract hiện có RideCompleted mẫu chưa chứa RiderId hoặc payment key. Cần hỏi E cách xác định người trả tiền và nghĩa vụ thanh toán trước khi thêm/loại trường. Chuỗi correlation trong JSON chỉ là metadata; W3C trace context phải được truyền đúng qua transport/header, không giả định nó luôn cùng kiểu với EventId.

### 9.2 Quy tắc không được bỏ qua

1. Amount là số tiền server đã xác nhận theo D02; Payment không tin giá do client gửi. Snapshot giá/currency không bị thay bởi rematch trái chính sách.
2. EventId nhận diện một integration event đã tạo; retry publish cùng intent giữ nguyên EventId. Một nghĩa vụ thanh toán còn cần payment key ổn định; dedup chỉ theo EventId không đủ chống hai event khác ID cùng yêu cầu thu một khoản.
3. Outbox đảm bảo DB và intent nguyên tử; broker ack không đồng nghĩa Payment đã thu tiền. Trip chỉ Paid sau event kết quả hợp lệ.
4. Consumer dedup dùng unique constraint/transaction, không chỉ kiểm tra if-exists trong bộ nhớ. Message xử lý thất bại chưa được ghi là đã hoàn thành.
5. Event không tìm thấy Trip hoặc sai amount/currency/key phải có cách xử lý/retry/error queue rõ ràng; không âm thầm acknowledge rồi bỏ dữ liệu cần đối soát.
6. Retry/redelivery hữu hạn, error queue có người/cách kiểm tra; không tự tạo payment key mới mỗi lần retry. Payment sở hữu retry thu tiền nghiệp vụ; Trip không đồng thời kích hoạt luồng thu tiền cạnh tranh.
7. Test restart sau commit trước publish và sau cập nhật Trip trước ack; kết quả nghiệp vụ không mất và không lặp.
8. Không tự cam kết toàn hệ thống exactly-once; mục tiêu là hiệu ứng nghiệp vụ idempotent trên giao nhận có thể lặp.

### 9.3 Ranh giới phối hợp

| Bên | Đầu ra cần cung cấp cho phần Trip | Đầu ra Trip cần cung cấp |
|---|---|---|
| Matching/Pricing | Contract HTTP, quote/candidate hợp lệ, no-driver, timeout và expiry behavior | Input hợp lệ, danh sách driver loại trừ, cách xử lý confirm/rematch |
| Payment (E theo EventContract) | Consumer RideCompleted, idempotency thu tiền, PaymentSettled/Failed | TripId/actor/amount/currency/payment key đúng và ổn định |
| Messaging/QA/DevOps (F theo EventContract) | DTO chung được review, config broker, queue/retry/error queue và test harness/CI phối hợp | Publisher/consumer Trip, schema Outbox/Inbox thống nhất và test nghiệp vụ |
| Frontend/Gateway | Cách gọi API và hiển thị pending/error, truyền token/request key | API/DTO/error contract, phân quyền và trạng thái có thể hiển thị |

Khi chờ DTO cuối cùng: tiếp tục domain, persistence và fake-client test. Không copy một bản event riêng vào Trip rồi coi là đã tích hợp với Payment.

## 10. Danh sách bug và điểm cần kiểm chứng

Mức độ dưới đây là ưu tiên trong đồ án, không phải điểm đánh giá bảo mật chính thức. `Đã xác minh code` nghĩa là đọc được hành vi trong source; không đồng nghĩa đã khai thác trên DB đang chạy.

| ID | Ưu tiên/tình trạng | Vị trí và vấn đề | Hướng xử lý, test đóng lỗi |
|---|---|---|---|
| BUG-01 | P0; đã xác minh code | AuthService.RegisterAsync nhận Role bất kỳ đang tồn tại, gồm Admin | Allowlist role đăng ký hoặc bắt buộc Rider; role cao chỉ qua API quản trị. Test đăng ký Role=Admin không nhận quyền Admin |
| BUG-02 | P0; đã tái hiện Gateway | Fallback policy yêu cầu authenticated, route auth/admin-login không được đánh dấu anonymous tại Gateway; 3 smoke request trả 401 | Cấu hình anonymous đúng cho YARP route public, giữ auth cục bộ ở Trip; test credentials hợp lệ/sai và /auth/me vẫn cần token |
| BUG-03 | P0 cho local E2E; đã xác minh config | Gateway trỏ trip-service:8080, matching-service:8080, payment-service:8080 nhưng Compose chưa có các service container này | Chọn chạy host thì override localhost/port; chạy cùng workspace thì dùng địa chỉ/port thực; hoặc F thêm app container. Xác nhận launch profile không đẩy Trip sang port 5175 ngoài dự kiến |
| BUG-04 | P1; đã xác minh code | EnsureCreated không cung cấp đường nâng cấp schema khi thêm Trips; chưa có migrations | Kế hoạch M1, test DB trắng và DB RBAC cũ; không reset DB cần giữ dữ liệu |
| BUG-05 | P1; đã xác minh code/config | JWT key và connection password dev được đặt trực tiếp trong config/fallback source | Externalize và fail rõ khi thiếu config; không dùng fallback secret. Nếu key thật từng được dùng/chia sẻ thì thay key và đánh giá token cũ |
| BUG-06 | P1; đã xác minh code, cần chốt D10 | IsActive chỉ chặn login, quyền nằm trong token đã phát vẫn dùng tới expiry | Chốt cơ chế kiểm tra/thu hồi hoặc ghi rõ hiệu lực trì hoãn; regression khóa user/thu hồi quyền với token đã cấp |
| BUG-07 | P1; đã xác minh code | DTO Auth thiếu validation; đăng ký lưu user và role bằng hai SaveChanges không có transaction bao trùm | Validate/null/độ dài/email/password; transaction; test lỗi bước sau không để user dở dang, concurrent đăng ký trùng không thành 500 |
| BUG-08 | P1; đã xác minh code | Catalog có trips.admin, payments.admin, users.read nhưng seeder chưa nạp; seeder thêm lại permission mặc định mỗi startup | Đồng bộ catalog/seed và chính sách seed-upgrade; restart không hoàn tác thay đổi RBAC ngoài ý muốn; test role được ủy quyền dùng quyền đã cấu hình |
| BUG-09 | P1; đã xác minh code | Startup catch lỗi seed rồi log sẽ nạp khi DB sẵn sàng nhưng không có cơ chế retry; health/live vẫn 200 | Liveness riêng, readiness kiểm tra phụ thuộc cần thiết; lỗi config/schema phải hiện rõ; transient retry có giới hạn nếu chọn |
| BUG-10 | P2; đã xác minh code | Gán role/permission bỏ qua mã không tồn tại; expiresIn login/register cố định 3600 dù lifetime có config | Reject input không hợp lệ, thống nhất expiry response với token; test role/code sai và lifetime khác 60 phút |
| BUG-11 | P1; đã xác minh build audit | NU1902/NU1903 cho System.Security.Cryptography.Pkcs 9.0.11, build hiện có 8 warning lặp theo project | Xác định dependency trực tiếp/bắc cầu, chọn bản sửa tương thích, restore/build/test lại; ghi rõ nếu chưa xử lý |
| BUG-12 | P1 về độ bao phủ; đã xác minh source | TripService.Tests có 1 test rỗng; admin trips đang mock | Thay test bằng case nghiệp vụ; query admin thật; không coi baseline xanh là bằng chứng hoàn thành Trip |

Lỗi mới tạo thêm BUG-13 trở đi kèm bước tái hiện. Không đánh dấu đã fix khi mới chỉnh code mà chưa có regression test phù hợp.

## 11. Ma trận kiểm thử cần đạt

Đây là tiêu chí theo hành vi, không phải yêu cầu phải có đúng một test cho mỗi dòng. Một nhóm có thể cần nhiều test data. Không đặt phần trăm coverage hoặc số test tối thiểu mà đề không quy định.

| Nhóm | Case cần chứng minh | Loại test | Liên hệ |
|---|---|---|---|
| T01 | Tạo Trip hợp lệ, RiderId từ token, dữ liệu âm/thiếu/tọa độ sai bị chặn | Domain + API | R02, M3 |
| T02 | Đủ chuỗi Requested -> DriverAccepted -> EnRoute -> PickedUp -> PaymentPending -> Paid, có history DroppedOff | Domain + integration | R05-R06 |
| T03 | Pickup trước accept, dropoff trước pickup, trạng thái cuối nhận lệnh sai đều bị từ chối | Domain + API | R05 |
| T04 | Rider từ chối giá thành Cancelled; confirm quote sai/hết hạn không mở offer | Domain + application | R03 |
| T05 | Driver chưa được mời/offer cũ/offer hết hạn không nhận hoặc từ chối thay người khác | API + persistence | R04, R09 |
| T06 | Driver reject <=k rematch, >k Failed; reject lặp không tăng đếm; no-driver có kết quả rõ | Domain + client integration | R04 |
| T07 | Response Matching đến sau hủy/quote mới không hồi sinh hoặc ghi đè Trip | Client + persistence | R03-R04 |
| T08 | Timeout/lỗi tạm thời có retry hữu hạn; 4xx nghiệp vụ không retry; không giữ DB transaction chờ mạng | Client integration | R10 |
| T09 | Rider A không xem/hủy Trip B; Driver khác không pickup/dropoff; list không lộ dữ liệu | API trực tiếp + Gateway | R09 |
| T10 | Thiếu token 401, thiếu permission 403; public login/register đi qua Gateway; admin vẫn bị bảo vệ | API + Gateway | BUG-01, BUG-02 |
| T11 | Hai request accept cùng lúc chỉ một winner; accept/cancel không ghi đè; state/history nhất quán | SQL Server thật + API | M1, M3 |
| T12 | Cùng request key/payload trả kết quả cũ; khác payload 409; actor khác không dùng kết quả của nhau | API + SQL Server | M3 |
| T13 | Dropoff lặp không tạo thêm intent; Amount/Currency chính xác và không lấy từ client | Application + messaging | R06-R07 |
| T14 | DB rollback không để lại event; crash/restart sau commit vẫn publish; broker offline phục hồi được | SQL Server + RabbitMQ thật | M5 |
| T15 | PaymentSettled đúng chuyển Paid; trùng/cạnh tranh không lặp history; sai amount/key/currency không Paid | Consumer + SQL Server | R06-R07 |
| T16 | PaymentFailed không Paid; Failed đến sau Settled không hạ Paid; unknown Trip/error có đường xử lý | Consumer + messaging | R07 |
| T17 | Redelivery giữ payment key; Payment nhận event trùng vẫn chỉ có một giao dịch thu tiền | E2E phối hợp E/F | R07 |
| T18 | Schema trắng + nâng cấp RBAC cũ; seed lặp không nhân đôi/hoàn tác quyền; dữ liệu sống qua restart | SQL Server thật | M1, BUG-04/08 |
| T19 | Đăng ký Admin bị chặn, validation lỗi 400; rollback gán role; token cũ khi khóa đúng D10 | Auth regression | BUG-01/06/07/10 |
| T20 | Correlation xuyên Gateway/Trip/Payment; log không chứa token/secret; readiness phản ánh phụ thuộc | E2E + quan sát log | R11, BUG-09 |

Tầng test dự kiến: `tests/TripService.Tests/Domain`, `Application`, `Api`, `Persistence`, `Messaging`; tên thư mục có thể điều chỉnh. Unit test không cần Docker; SQL-specific và broker integration phải được chạy ở job/môi trường có hạ tầng tương ứng. Test fake/harness không thay thế toàn bộ test transport thật.

## 12. Runbook kiểm tra và demo

### 12.1 Trước khi chạy

- [ ] **RUN-01** Xác nhận .env/local secrets đã cấu hình; không in giá trị secret khi kiểm tra.
- [ ] **RUN-02** Chọn topology và override downstream theo BUG-03; kiểm tra port từ launchSettings khi dotnet run.
- [ ] **RUN-03** Chạy hạ tầng theo Compose; kiểm tra readiness DB và broker.
- [ ] **RUN-04** Áp dụng đường nâng cấp schema đã review ở M1; không trộn EnsureCreated và migrations.
- [ ] **RUN-05** Seed tài khoản demo trong môi trường Development; tạo thêm rider/driver thứ hai để test ownership/cạnh tranh.

Các lệnh từ thư mục gốc repo, sau khi đã có cấu hình môi trường phù hợp:

```powershell
docker compose -f .devcontainer/compose.yaml up -d
dotnet restore SwiftRide.sln
dotnet build SwiftRide.sln -c Release --no-restore
dotnet test SwiftRide.sln -c Release --no-build
```

Nếu chạy trên host, mỗi service trong một terminal:

```powershell
dotnet run --project src/TripService --no-launch-profile --urls http://localhost:5001
dotnet run --project src/MatchingService --no-launch-profile --urls http://localhost:5002
dotnet run --project src/PaymentService --no-launch-profile --urls http://localhost:5003
dotnet run --project src/ApiGateway --no-launch-profile --urls http://localhost:5000
```

Các lệnh run trên không tự sửa Gateway cluster/config connection/broker. M0 phải cung cấp override theo topology trước; ví dụ host dùng localhost còn container dùng hostname trên đúng mạng. Compose hiện tại chủ yếu khởi động hạ tầng và workspace, không tự chạy bốn ASP.NET app.

Không cung cấp lệnh database update để chạy ngay trên TripDB cũ: trước tiên M1 phải xác minh và xử lý baseline schema. Lệnh migration cụ thể và project tool manifest sẽ được ghi sau khi phương án nâng cấp đã được kiểm tra.

### 12.2 Kịch bản nghiệm thu qua Gateway

| Kịch bản | Thao tác | Kết quả/bằng chứng cần lưu |
|---|---|---|
| DEMO-01: thành công | Login rider -> tạo -> nhận quote -> confirm -> driver accept/en-route/pickup/dropoff -> PaymentSettled | Trip Paid, history đúng, một nghĩa vụ thanh toán; log/trace theo TripId và correlation |
| DEMO-02: rider từ chối | Tạo -> có quote -> từ chối/hủy | Cancelled, không có RideCompleted và không thu tiền |
| DEMO-03: driver từ chối | Từ chối ở các lần trong ngưỡng rồi vượt ngưỡng | Candidate khác, offer cũ vô hiệu, cuối cùng Failed đúng cấu hình k |
| DEMO-04: sai quyền/trạng thái | Rider khác đọc/hủy; driver khác dropoff; pickup trước accept | 403/404 hoặc 409 theo contract, DB và history không đổi |
| DEMO-05: lặp/đồng thời | Gửi accept cạnh tranh, dropoff lặp, redeliver payment event | Một kết quả nghiệp vụ hợp lệ, không thu tiền/history trùng |
| DEMO-06: gián đoạn | Tạm ngắt broker trong môi trường test sau khi dropoff; phục hồi | Pending + intent bền vững, sau đó publish/consume và Paid, không sửa DB thủ công |
| DEMO-07: thanh toán lỗi | E giả lập thất bại sau retry theo contract | Trip cần xử lý, chưa Paid; recovery theo cùng payment key và quy trình đã chốt |

Lưu command/test report, response đã bỏ token, migration/schema, log broker/consumer cần thiết và ảnh trạng thái DB không chứa dữ liệu nhạy cảm. Không coi request do Admin thực hiện thay toàn bộ luồng Rider/Driver là demo đạt yêu cầu ownership.

## 13. Definition of Done

- [ ] **DONE-01** R02-R07 có code và test, bao gồm nhánh rider từ chối, driver vượt k và thanh toán lỗi.
- [ ] **DONE-02** Các Trip endpoint chính chạy thật, không còn 501 hoặc dữ liệu mock để giả lập hoàn tất.
- [ ] **DONE-03** State pattern và trách nhiệm các tầng thể hiện trong code; không đọc database của service khác.
- [ ] **DONE-04** Schema được nâng cấp có kiểm soát; Auth/RBAC giữ hoạt động và concurrency/idempotency đã được test.
- [ ] **DONE-05** RideCompleted/PaymentSettled tương tác qua RabbitMQ thật; cách xử lý PaymentFailed hoặc phương án failure tương đương đã thống nhất và demo được.
- [ ] **DONE-06** Outbox/Inbox theo phạm vi M5 được kiểm tra với rollback/restart/redelivery; không tuyên bố exactly-once delivery.
- [ ] **DONE-07** BUG-01/02/03 và các lỗi ảnh hưởng quyền truy cập, tiền, state đã đóng; lỗi hoãn có lý do, tác động và người theo dõi.
- [ ] **DONE-08** Build/test Release đạt; có bằng chứng SQL Server/broker integration, không chỉ unit test hoặc test rỗng.
- [ ] **DONE-09** Hướng dẫn chạy từ checkout mới đúng topology; source không chứa secret thực; báo cáo nêu rõ JWT local/OIDC và các trade-off đã chọn.
- [ ] **DONE-10** M7 demo/bàn giao đạt và bảng tiến độ có bằng chứng thực tế.

## 14. Nhật ký tiến độ và phần việc hoãn

### Nhật ký mỗi phiên

| Ngày | Mã việc | Đã làm / kết quả test | Commit/PR hoặc bằng chứng | Việc tiếp theo / vướng mắc |
|---|---|---|---|---|
| 26/10/2026 | Khảo sát baseline | Build Release thành công; 9 test pass, trong đó 1 Trip test rỗng; 3 Gateway public smoke đều 401; chưa kiểm thử DB/broker | Mã nguồn b04118c + working tree và kết quả lệnh ghi ở mục 2 | Bắt đầu M0; chưa có mốc triển khai mới được nghiệm thu |
| 30/10/2026 | Giao diện Rider và cấu hình Windows | Đăng nhập qua Gateway đã được kiểm tra thủ công; home có Leaflet/OpenStreetMap, chọn và đổi điểm đón/đến bằng bản đồ. Có nút Tìm và vùng kết quả, chưa triển khai tìm địa chỉ. Gateway Development dùng localhost; Trip có UserSecretsId. Chưa tạo entity/API nghiệp vụ Trip hoặc event publishing. | Nhánh feat/trip-rider-ui; đăng nhập và chọn vị trí do người thực hiện xác nhận | Tiếp tục hàm tìm địa chỉ Nominatim theo chế độ bấm Tìm, cache và giới hạn tần suất; sau đó hiển thị kết quả để chọn |
| ... | ... | ... | ... | ... |

### Theo dõi phụ thuộc

| Phụ thuộc | Nội dung chờ | Người/bên phụ trách | Việc có thể tiếp tục trong lúc chờ | Kết quả/ngày chốt |
|---|---|---|---|---|
| Event contract | D07-D09, actor/payment key, failure semantics | Trip + E + F | Domain/persistence/fake consumer test | Chưa chốt |
| Matching contract | Quote expiry, candidate, rematch, offer identity | Trip + Matching | Unit test state, fake HTTP client | Chưa chốt |
| Môi trường E2E | Topology, DB schema cần giữ, CI dependencies | Trip + F | M2 và thiết kế migration | Chưa xác nhận |

### Việc hoãn/mở rộng

| Hạng mục | Vì sao chưa làm trong luồng chính | Điều kiện đưa vào |
|---|---|---|
| Saga đa service và bồi thường nâng cao | Không thay thế local transaction/Outbox; tránh kéo toàn bộ Payment vào Trip | Luồng chính ổn định, nhóm thống nhất các bước bồi thường |
| TripAccepted/TripCancelled và event khác | Chưa có contract/consumer được chốt | Có nhu cầu và hợp đồng cụ thể |
| Real-time vị trí, bản đồ, thông báo push | Ngoài phần Trip API/Event/Tests hiện được yêu cầu | Sau nghiệm thu M7 |
| Dashboard/metrics nghiệp vụ nâng cao | OTel nền đã có; ưu tiên trace và luồng có thể chứng minh trước | Có thời gian và chủ sở hữu rõ |
| Tách thêm service hoặc project tầng riêng | Không cần để hoàn thành nghiệp vụ; giới hạn ba service cốt lõi | Chỉ điều chỉnh có lý do và không vượt phạm vi đề |

**Việc đầu tiên nên thực hiện:** chốt D01-D11, sửa BUG-01/02/03, sau đó tạo khung domain Trip và lập đường chuyển schema ở M1. Không bắt đầu bằng việc tạo hàng loạt bảng hoặc publish event khi chưa có quy tắc chuyển trạng thái và contract đủ rõ.
