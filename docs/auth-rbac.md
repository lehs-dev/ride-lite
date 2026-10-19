# Hướng Dẫn Xác Thực & Phân Quyền Động (Auth & Dynamic RBAC Guide)

Tài liệu này giải thích chi tiết cấu trúc xác thực JWT Bearer, cơ chế **Phân quyền động (Dynamic RBAC)** được lưu trữ trong CSDL SQL Server (`TripDB`) của `TripService`, và cách cấu hình bảo mật cho toàn bộ hệ thống SwiftRide.

---

## 1. Kiến Trúc Xác Thực & Phân Quyền Động

Hệ thống tuân thủ nghiêm ngặt nguyên tắc **tối đa 3 microservices** theo tài liệu kiến trúc:
- **`TripService`**: Quản lý vòng đời chuyến đi và đảm nhiệm chức năng Định danh & Phân quyền động (Identity & Dynamic RBAC). Bền vững hóa toàn bộ bảng tài khoản và quyền hạn trong **SQL Server (`TripDB`)**.
- **`ApiGateway`**: Tiếp nhận request từ client (port 5000), định tuyến route `/auth/**` sang `TripService` (port 5001), áp dụng CORS, Rate Limiting và gắn header giám sát `X-Correlation-ID`.
- **`MatchingService`** & **`PaymentService`**: Xác thực chữ ký JWT thông qua thư viện chung `SwiftRide.Security` và bảo vệ tài nguyên bằng quyền hạn (`.RequirePermission(...)`).

```mermaid
flowchart TD
    Client["Client (Rider / Driver / Admin)"] --> Gateway["API Gateway (Port 5000)\n1. Correlation ID\n2. CORS\n3. Rate Limiter\n4. YARP Routing"]
    Gateway -->|/auth/** & /trips/**| TripSvc["TripService (Port 5001)\nSQL Server (TripDB)"]
    Gateway -->|/match/** & /quote/**| MatchSvc["Matching Service (Port 5002)\nMongoDB (MatchDB)"]
    Gateway -->|/payments/**| PaySvc["Payment Service (Port 5003)\nPostgreSQL (PayDB)"]

    TripSvc -->|EF Core SqlServer| SQLDB[("SQL Server\nTripDB\n(Users, Roles, Permissions)")]
    TripSvc -.->|Phát hành JWT (HMAC-SHA256)| Client
```

---

## 2. Mô Hình Dữ Liệu Phân Quyền Động (Dynamic RBAC Schema)

Hệ thống triển khai mô hình 5 bảng quan hệ chuẩn:

```
  ┌───────────────┐          ┌───────────────┐          ┌───────────────┐
  │     Users     │◄────────►│   UserRoles   │◄────────►│     Roles     │
  ├───────────────┤  1     * ├───────────────┤ *     1  ├───────────────┤
  │ Id (Guid)     │          │ UserId (Guid) │          │ Id (Guid)     │
  │ Username      │          │ RoleId (Guid) │          │ Name (string) │
  │ PasswordHash  │          └───────────────┘          │ Description   │
  │ PasswordSalt  │                                     │ IsSystemRole  │
  │ FullName      │                                     └───────┬───────┘
  │ Email         │                                             │ 1
  │ IsActive      │                                             │
  │ CreatedAtUtc  │                                             ▼ *
  └───────────────┘                                     ┌───────────────┐
                                                        │RolePermissions│
                                                        ├───────────────┤
                                                        │ RoleId (Guid) │
                                                        │ PermissionId  │
                                                        └───────┬───────┘
                                                                │ *
                                                                │ 1
                                                        ┌───────▼───────┐
                                                        │  Permissions  │
                                                        ├───────────────┤
                                                        │ Id (Guid)     │
                                                        │ Code (unique) │
                                                        │ Description   │
                                                        │ Module        │
                                                        └───────────────┘
```

### 2.1. Danh mục Quyền cơ sở (Permissions Catalog)

Được tự động nạp vào CSDL (`TripDbSeeder`) khi khởi động:

| Mã quyền (`Code`) | Module | Mô tả |
| :--- | :--- | :--- |
| `trips.create` | Trips | Tạo yêu cầu đặt chuyến đi mới |
| `trips.read` | Trips | Xem chi tiết hoặc danh sách chuyến đi |
| `trips.accept` | Trips | Tài xế nhận chuyến đi |
| `trips.update_status` | Trips | Cập nhật mốc trạng thái (đang đến, đón, trả khách) |
| `trips.cancel` | Trips | Hủy chuyến đi |
| `matching.quote` | Matching | Yêu cầu báo giá ước tính |
| `matching.match` | Matching | Tìm kiếm và ghép nối tài xế xung quanh |
| `payments.create` | Payments | Tạo giao dịch thanh toán chuyến đi |
| `payments.read` | Payments | Xem thông tin giao dịch thanh toán |
| `payments.refund` | Payments | Thực hiện hoàn tiền giao dịch |
| `roles.manage` | Identity | Tạo vai trò mới, gán quyền cho vai trò tại runtime |
| `users.manage` | Identity | Quản lý người dùng và phân quyền vai trò |

### 2.2. Các Vai trò Mặc định (Default Roles)
- **`Admin`**: Có toàn bộ quyền trong hệ thống.
- **`Rider`**: Gán các quyền: `trips.create`, `trips.read`, `trips.cancel`, `matching.quote`, `payments.create`.
- **`Driver`**: Gán các quyền: `trips.read`, `trips.accept`, `trips.update_status`, `matching.match`.

### 2.3. Tài khoản Kiểm thử Mẫu (Default Seeded Users)
Mật khẩu được băm an toàn bằng PBKDF2 (SHA-256 kèm muối ngẫu nhiên 16 bytes, 100.000 vòng lặp):

| Username | Mật khẩu | Vai trò | Mục đích |
| :--- | :--- | :--- | :--- |
| `test-rider` | `Rider@123` | `Rider` | Kiểm thử đặt chuyến, xem giá, thanh toán |
| `test-driver` | `Driver@123` | `Driver` | Kiểm thử nhận chuyến, đổi trạng thái chuyến |
| `admin` | `Admin@123` | `Admin` | Quản trị viên, tạo vai trò mới và phân quyền động |

---

## 3. Cấu Trúc JWT Token & Đánh Giá Quyền Tại Microservices

### 3.1. Cấu trúc Payload Token
Khi người dùng đăng nhập thành công, `TripService` sinh JWT mang các claims:
```json
{
  "sub": "b2f6b840-7e39-4d2b-a36f-5b1234567890",
  "name": "test-rider",
  "email": "rider@swiftride.local",
  "role": "Rider",
  "permission": [
    "trips.create",
    "trips.read",
    "trips.cancel",
    "matching.quote",
    "payments.create"
  ],
  "iss": "SwiftRide",
  "aud": "swiftride-api",
  "exp": 1728892800
}
```

### 3.2. Đánh giá quyền tại các Service (`SwiftRide.Security`)
Các endpoint trong minimal API sử dụng phương thức mở rộng `.RequirePermission(...)`:
```csharp
app.MapPost("/trips", CreateTripHandler)
   .RequirePermission(PermissionsCatalog.TripsCreate);

app.MapPost("/trips/{id:guid}/driver-accept", DriverAcceptHandler)
   .RequirePermission(PermissionsCatalog.TripsAccept);
```
- Middleware kiểm tra: Nếu User có role `Admin` hoặc sở hữu claim `permission: <mã_quyền>` -> Cho phép (`200 OK`).
- Nếu thiếu quyền -> Trả về `403 Forbidden`.
- Nếu chưa đăng nhập -> Trả về `401 Unauthorized`.

---

## 4. Hướng Dẫn Sử Dụng API Xác Thực & Quản Trị RBAC

Tất cả các API dưới đây được gọi qua **API Gateway (Port 5000)**:

### 4.1. Đăng nhập (`POST /auth/login`)
```bash
curl -X POST "http://localhost:5000/auth/login" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "test-rider",
    "password": "Rider@123"
  }'
```
Response:
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "tokenType": "Bearer",
  "expiresIn": 3600,
  "user": {
    "id": "...",
    "username": "test-rider",
    "fullName": "Nguyen Van Rider",
    "email": "rider@swiftride.local",
    "roles": ["Rider"],
    "permissions": ["trips.create", "trips.read", "trips.cancel", "matching.quote", "payments.create"]
  }
}
```

### 4.2. Đăng ký tài khoản mới (`POST /auth/register`)
Mặc định đăng ký mới sẽ được gán role `Rider`:
```bash
curl -X POST "http://localhost:5000/auth/register" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "new-rider",
    "password": "MyPassword@123",
    "fullName": "Le Van New",
    "email": "newrider@example.com",
    "role": "Rider"
  }'
```

### 4.3. Xem thông tin user hiện tại (`GET /auth/me`)
```bash
curl -X GET "http://localhost:5000/auth/me" \
  -H "Authorization: Bearer <access_token>"
```

### 4.4. Quản trị Phân quyền Động (Dành cho Admin)

#### Xem danh sách quyền hệ thống:
```bash
curl -X GET "http://localhost:5000/auth/permissions" \
  -H "Authorization: Bearer <admin_token>"
```

#### Xem danh sách vai trò và quyền tương ứng:
```bash
curl -X GET "http://localhost:5000/auth/roles" \
  -H "Authorization: Bearer <admin_token>"
```

#### Tạo vai trò mới tại runtime:
```bash
curl -X POST "http://localhost:5000/auth/roles" \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Dispatcher",
    "description": "Điều phối viên trung tâm",
    "permissionCodes": ["trips.read", "trips.cancel", "matching.quote", "matching.match"]
  }'
```

#### Cập nhật quyền cho vai trò bất kỳ:
```bash
curl -X POST "http://localhost:5000/auth/roles/<role_id>/permissions" \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "permissionCodes": ["trips.read", "trips.cancel"]
  }'
```

#### Gán vai trò cho người dùng:
```bash
curl -X POST "http://localhost:5000/auth/users/<user_id>/roles" \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "roleNames": ["Dispatcher"]
  }'
```

---

### 4.5. Phân Luồng & Cổng Đăng Nhập Dành Riêng Cho Quản Trị Viên (Admin Dedicated Flow)

Để tối ưu bảo mật và tách biệt bề mặt tấn công giữa người dùng thông thường và quản trị viên:
1. **Cổng Đăng nhập Admin riêng biệt (`POST /admin/auth/login`)**:
   - Được bảo vệ bởi Rate Limiter chống brute-force khắt khe (`5 requests / 60 giây`).
   - Kiểm tra vai trò ngay lúc đăng nhập: Nếu user không có role `Admin`, hệ thống **từ chối ngay lập tức** (`403 Forbidden` - *"Tài khoản không có quyền truy cập trang quản trị"*), tuyệt đối không cấp token.
   - Thời hạn Token ngắn hơn (30 phút) để giảm thiểu rủi ro khi quên đăng xuất.

```bash
# Đăng nhập Admin qua Gateway
curl -X POST "http://localhost:5000/admin/auth/login" \
  -H "Content-Type: application/json" \
  -d '{
    "username": "admin",
    "password": "Admin@123"
  }'
```

2. **Các API Quản trị & Giám sát Hệ thống**:

#### Tra cứu danh sách người dùng toàn hệ thống (Hỗ trợ lọc theo Role hoặc tìm kiếm từ khóa):
```bash
curl -X GET "http://localhost:5000/admin/users?role=Driver&keyword=Tran" \
  -H "Authorization: Bearer <admin_token>"
```

#### Khóa hoặc mở khóa tài khoản người dùng (Chặn Admin tự khóa tài khoản của chính mình):
```bash
curl -X PATCH "http://localhost:5000/admin/users/<target_user_id>/status" \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "isActive": false
  }'
```

#### Giám sát toàn bộ chuyến đi trên toàn hệ thống:
```bash
curl -X GET "http://localhost:5000/admin/trips" \
  -H "Authorization: Bearer <admin_token>"
```

#### Kiểm toán toàn bộ sổ cái và giao dịch thanh toán:
```bash
curl -X GET "http://localhost:5000/admin/payments" \
  -H "Authorization: Bearer <admin_token>"
```

---

## 5. Cấu Hình Service (`appsettings.json`)

Mọi service chia sẻ chung cấu hình `Authentication` để kiểm tra chữ ký token:

```json
{
  "Authentication": {
    "Issuer": "SwiftRide",
    "Audience": "swiftride-api",
    "SecretKey": "SuperSecretKeyForDevelopmentAndTestingSwiftRide2026!@",
    "RequireHttpsMetadata": false,
    "TokenLifetimeMinutes": 60
  }
}
```

> **Lưu ý bảo mật khi triển khai Production**:
> Khóa `SecretKey` phải có độ dài tối thiểu 256 bits (32 bytes) và được cấu hình qua biến môi trường hoặc Azure Key Vault / AWS Secrets Manager, không lưu trực tiếp khóa production trong source code.
