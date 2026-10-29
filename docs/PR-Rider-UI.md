Thêm giao diện Rider để thử đăng nhập qua Gateway và chọn điểm đón/đến trên bản đồ. Cấu hình phát triển hỗ trợ chạy các ứng dụng .NET trực tiếp trên Windows, còn database và broker chạy bằng Docker.

PR đang ở giai đoạn chuẩn bị giao diện và môi trường; chưa hoàn thành nghiệp vụ TripService.

## Thay đổi

- Trang đăng nhập SwiftRide bằng HTML/CSS/JavaScript, kiểm tra ô trống và gọi `POST /auth/login` qua Gateway để hiển thị họ tên người dùng.
- Trang home có bố cục đặt xe, Leaflet/OpenStreetMap và chọn điểm đón/đến bằng cách chọn ô nhập rồi bấm bản đồ. Marker và tọa độ cập nhật khi chọn lại vị trí.
- Chuẩn bị nút Tìm và vùng hiển thị kết quả cho điểm đón. Chưa có hàm gọi Nominatim hoặc danh sách chọn địa chỉ.
- Gateway Development trỏ đến các service trên `localhost:5001/5002/5003`; route `/auth/**` cho phép anonymous ở Gateway và tiếp tục áp dụng bảo vệ endpoint tại TripService. CORS bổ sung `http://127.0.0.1:5500` cho Live Server.
- Bổ sung `POSTGRES_HOST_PORT` và hướng dẫn đổi port host khi PostgreSQL trên Windows đã chiếm cổng 5432.
- TripService có `UserSecretsId` để nạp cấu hình riêng khi chạy Development. Không đưa `.env` hoặc giá trị User Secrets vào commit.
- Bổ sung bản đề xuất Event Contract và kế hoạch TripService, có đối chiếu PDF môn học, checklist, tiêu chí nghiệm thu và nhật ký tiến độ.

## Kiểm tra

- `dotnet build SwiftRide.sln -c Release --nologo --verbosity minimal`: thành công, 0 error. Còn 8 cảnh báo NU1902/NU1903 liên quan `System.Security.Cryptography.Pkcs` 9.0.11.
- `dotnet test SwiftRide.sln -c Release --no-build --nologo --verbosity minimal`: 9 test pass; gồm 8 test Security và 1 test TripService rỗng. Kết quả này chưa chứng minh độ bao phủ nghiệp vụ chuyến đi.
- `node --check frontend/login.js` và `node --check frontend/home.js`: thành công.
- Đã thử thủ công trong quá trình phát triển: login thành công/sai mật khẩu/ô trống; bản đồ thật hiển thị; chọn lại marker điểm đón và điểm đến theo ô đang được chọn.

## Cách xem giao diện

- Chạy ứng dụng trên Windows trong môi trường Development; cấu hình kết nối SQL Server bằng User Secrets của máy phát triển.
- Mở `frontend/index.html` hoặc `frontend/home.html` bằng Live Server tại `http://127.0.0.1:5500`.
- Login cần Gateway cổng 5000 và TripService cổng 5001. Phần bản đồ cần Internet để nạp Leaflet và OpenStreetMap.
- Login hiện chỉ hiển thị kết quả; chưa lưu token hoặc tự chuyển sang trang home.

## Phần còn lại

- [ ] Thêm hàm tìm địa chỉ theo nút Tìm, có cache và giới hạn tần suất phù hợp chính sách Nominatim đã chọn cho bản thử nghiệm một người/một tab.
- [ ] Hiển thị danh sách kết quả; chọn kết quả để đặt marker và lưu tọa độ; áp dụng cho cả điểm đón và điểm đến.
- [ ] Bảo đảm địa chỉ sửa lại không tiếp tục sử dụng tọa độ cũ; thống nhất lưu/truyền token cho API chuyến đi.
- [ ] Triển khai Trip entity, State pattern, persistence/migrations và API tạo/đọc/hủy/cập nhật chuyến. Hai Trip endpoint hiện vẫn trả 501.
- [ ] Chốt Event Contract với Payment/Messaging, triển khai publishing/consuming và kiểm thử tích hợp.
- [ ] Thực hiện regression/security fixes và bổ sung test nghiệp vụ theo `TripService-Progress.md`.

Event Contract và các endpoint/bảng bổ sung trong kế hoạch vẫn là đề xuất cần thống nhất; PDF môn học là căn cứ nghiệp vụ chính. Luồng phải tạo `Requested` trước khi xác nhận báo giá và chỉ kết thúc thành công sau khi thanh toán thành công.
