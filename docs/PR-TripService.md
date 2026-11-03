# Bổ sung API tạo, xem chuyến đi và khung State pattern cho TripService

TripService đã lưu được chuyến mới ở trạng thái Requested và cho phép hành khách xem chuyến của mình. POST /trips thay thế phản hồi 501 trước đây; GET /trips/{id} trả 404 khi chuyến không tồn tại hoặc thuộc hành khách khác.

## Nội dung thay đổi
- Bổ sung entity Trip, trạng thái chuyến, trạng thái thanh toán và migration khởi tạo; các trường báo giá được phép chưa có giá trị, RiderId và DriverId liên kết tới Users.
- Chuyển quản lý cấu trúc database sang migrations; vô hiệu hóa EnsureCreated, giữ phần nạp dữ liệu mẫu.
- Bổ sung DTO, validation, application service và Repository với truy vấn chỉ đọc / truy vấn theo dõi thay đổi.
- Lấy mã hành khách từ token, kiểm tra quyền trips.create / trips.read và quyền sở hữu khi xem chuyến.
- Cấu hình Swagger với Bearer token.
- Bổ sung State pattern cho Requested và DriverAccepted cùng hành vi nhận chuyến trong entity. API nhận chuyến vẫn trả 501.
- Nâng System.Security.Cryptography.Pkcs lên 9.0.21 để xử lý cảnh báo bảo mật.
- Nhánh này cũng gồm giao diện đăng nhập/bản đồ hành khách và cấu hình Gateway local từ phần việc trước.

## Kiểm tra
- Build toàn solution ở Release: 0 lỗi, 0 cảnh báo.
- Các test hiện có đều pass: 8 test Security và 1 test mẫu TripService. Chưa có test tự động nghiệp vụ Trip mới; test mẫu không chứng minh độ bao phủ nghiệp vụ.
- Kiểm tra thủ công bằng Postman và SSMS: tạo chuyến trả 201 và lưu bản ghi; tọa độ sai hoặc thiếu trả 400, không thêm bản ghi; thiếu token trả 401; xem chuyến của mình trả 200; chuyến người khác hoặc mã không tồn tại trả 404.
- Migration đã áp dụng sau khi tạo lại TripDB local. Database cũ dùng EnsureCreated cần xử lý chuyển đổi riêng vì migration đầu tiên tạo cả các bảng phân quyền.

## Công việc còn lại
- Xác nhận báo giá, đề nghị tài xế, quyền nhận chuyến, chống nhận đồng thời và hoàn thiện vòng đời chuyến.
- Tích hợp Matching/Payment, phát và nhận event, test nghiệp vụ và test tích hợp.
- PaymentMethod hiện chỉ kiểm tra bắt buộc và độ dài. Demo là giá trị kiểm tra, chưa phải phương thức thanh toán được nhóm chốt.
- GET hiện chỉ phục vụ hành khách xem chuyến của mình; chưa có quyền xem của tài xế và quản trị viên.
