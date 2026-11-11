# TASK-035: Object Storage Subsystem (Local Disk, S3/MinIO), Image Processing & Media Browser Web Portal

- **Mã Task**: TASK-035
- **Tên Task**: Phân Hệ Lưu Trữ Đối Tượng (Local Disk, S3/MinIO), Xác Thực Magic Bytes, Xử Lý Ảnh Thumbnail, Thử Nghiệm "Send Test" Trực Tiếp & Trình Duyệt Media Browser Trên Web Portal
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-11
- **Tài Liệu Tham Chiếu**: [shared_services_design.md](../architecture/shared_services_design.md), [TASK-032-shared-services-core-architecture.md](./TASK-032-shared-services-core-architecture.md), [TASK-033-shared-services-email-subsystem.md](./TASK-033-shared-services-email-subsystem.md), [TASK-034-shared-services-chat-messaging-subsystem.md](./TASK-034-shared-services-chat-messaging-subsystem.md)

---

## 1. Vùng Làm Việc Cụ Thể (Working Context & Directories)

> [!IMPORTANT]
> **CHỈ DẪN KHÔNG GIAN LÀM VIỆC DÀNH CHO CLAUDE CODE**:
> Toàn bộ công việc của task này **chỉ diễn ra bên trong thư mục `repos/shared-services/`**.
> Tuyệt đối **KHÔNG tìm kiếm, quét hoặc sửa đổi** các thư mục khác ngoài phạm vi này (ví dụ: `repos/identity`, `repos/linguabridge-web`, `repos/linguabridge-mobile`, v.v.).

- **Solution Root**: `repos/shared-services/AppInHouse.SharedServices.sln` (.NET 10 LTS)
- **Các Thư Mục Dự Án Cần Tập Trung**:
  1. `repos/shared-services/src/Core/AppInHouse.SharedServices.Domain/` *(Entity StoredFile, Enums, ProviderCatalog)*
  2. `repos/shared-services/src/Core/AppInHouse.SharedServices.Application/` *(Tạo thư mục mới `Storage/`: IStorageProvider, StorageDispatcher, Validators, MagicBytes, ImageProcessing)*
  3. `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Infrastructure/` *(Tạo thư mục mới `Storage/`: LocalDiskStorageProvider, S3StorageProvider, MockStorageProvider, ImageThumbnailService)*
  4. `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Persistence/` *(StoredFileRepository, MediaLogRepository, schema `shared.stored_files` & `shared.media_logs`)*
  5. `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Api/` *(Endpoints: Upload, Download/Stream, Thumbnail, Delete, List, Test)*
  6. `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Web/` *(Trang `/Storage`: Form cấu hình Local/S3, Nút Send Test, Media Browser Gallery & Upload)*
  7. `repos/shared-services/src/Sdk/AppInHouse.SharedServices.ClientSdk/` *(Bổ sung các hàm Upload/Download/Delete/List/Test vào ISharedServicesClient)*
  8. `repos/shared-services/tests/AppInHouse.SharedServices.UnitTests/` *(Unit tests nghiệp vụ Storage)*
  9. `repos/shared-services/tests/AppInHouse.SharedServices.IntegrationTests/` *(Integration tests API và Web Portal)*

---

## 2. Mục Tiêu Kỹ Thuật

Triển khai phân hệ lưu trữ đối tượng tập trung (**Object Storage Subsystem**) dùng chung cho toàn bộ hệ sinh thái AppInHouse (Identity lưu avatar, LinguaBridge lưu hình ảnh bài học/audio phát âm, các web dự án tương lai lưu tài liệu đính kèm):

1. **Phân Tách Rõ Ràng Giữa Storage Profile & Stored File (Nguyên Tắc P1)**:
   - `ChannelConfiguration` (Kênh `Storage`):
     - `ProviderType.LocalDisk`: Lưu trữ cục bộ trên thư mục máy chủ (Cấu hình: `RootPath`, `PublicUrlBase`, `MaxFileSizeMb`).
     - `ProviderType.S3`: Lưu trữ đám mây tương thích S3 (AWS S3, MinIO, Cloudflare R2). Cấu hình Settings: `Bucket`, `Region`, `ServiceUrl` (endpoint MinIO/R2), `ForcePathStyle`, `PublicUrlBase`. Credential `AccessKeyId` trong settings, `SecretAccessKey` được bảo vệ mã hóa trong `EncryptedSecret`.
     - `ProviderType.Mock`: Giả lập lưu trữ trong RAM cho môi trường kiểm thử dev.
   - `StoredFile` (Metadata tệp tin trong database):
     - `Id`: UUID.
     - `ProjectCode`: Mã dự án sở hữu tệp (ví dụ `identity`, `linguabridge`, `default`).
     - `StorageProfileId`: ID của cấu hình storage đang lưu trữ file.
     - `StorageProvider`: Nhà cung cấp thực tế đang chứa tệp (`LocalDisk` hoặc `S3`). Đảm bảo khi đổi Provider ở cấu hình thì file cũ vẫn biết nơi nó thực sự đang nằm để đọc/xóa.
     - `ObjectKey`: Khóa lưu trữ vật lý do **Server sinh ngẫu nhiên** theo quy tắc: `uploads/{project_code}/{yyyy}/{MM}/{guid}{ext}`. **Tuyệt đối không dùng tên file do người dùng gửi lên làm đường dẫn vật lý (chống Path Traversal)**.
     - `OriginalFileName`: Tên gốc sau khi đã lọc bỏ ký tự nguy hiểm (`Path.GetFileName`).
     - `MimeType`: Loại MIME đã được xác thực qua Magic Bytes.
     - `FileSizeBytes`: Kích thước tệp tin.
     - `FileHashSha256`: Mã băm SHA-256 tính toán trong quá trình stream.
     - `IsPublic`: Cờ xác định tệp công khai hay cần quyền truy cập.
     - `CreatedAt`: Thời gian tải lên.
2. **Xác Thực Magic Bytes & Chống Giả Mạo Định Dạng (P1 Security)**:
   - Xây dựng bộ kiểm tra **Magic Bytes**: Đọc header bytes thực tế của stream để xác thực tệp tin:
     - `image/jpeg` (`FF D8 FF`)
     - `image/png` (`89 50 4E 47 0D 0A 1A 0A`)
     - `image/webp` (`52 49 46 46 ... 57 45 42 50`)
     - `image/gif` (`47 49 46 38`)
     - `application/pdf` (`25 50 44 46`)
     - `audio/mpeg` / `audio/ogg` / `audio/wav` (tùy chọn)
   - Nếu Client gửi `Content-Type: image/png` nhưng file thực chất là file thực thi `.exe` hoặc script độc hại: **Từ chối ngay lập tức với HTTP 400 Bad Request**.
   - Giới hạn kích thước file tải lên (mặc định 15MB, cấu hình được qua `MaxFileSizeMb`).
3. **Xử Lý Ảnh & Tạo Thumbnail An Toàn (Image Processing)**:
   - Xây dựng `IImageThumbnailService`:
     - Tự động tạo ảnh thu nhỏ (Thumbnail, kích thước chuẩn ~256x256 pixel hoặc WebP tối ưu) khi tệp tin tải lên là ảnh (`image/png`, `image/jpeg`, `image/webp`).
     - Lưu trữ thumbnail song song: `uploads/{project_code}/{yyyy}/{MM}/{guid}_thumb.webp`.
     - Chống bom giải nén (Decompression Bomb / Pixel Flood): Kiểm tra kích thước chiều rộng/chiều cao tối đa (ví dụ tối đa 8000x8000 pixel), nếu vượt ngưỡng lập tức từ chối trước khi giải nén toàn bộ bộ nhớ.
     - Triển khai bằng thư viện .NET tiêu chuẩn hoặc SkiaSharp / SixLabors.ImageSharp phù hợp.
4. **Nghiệp Vụ "Send Test / Test Connection" Độc Lập Trực Tiếp Từ Bộ Nhớ (P0 Requirement)**:
   - Endpoint: `POST /api/v1/storage/test`:
     - Chế độ 1: Thử nghiệm cấu hình đang nhập trong RAM:
       - Với `LocalDisk`: Kiểm tra quyền ghi/đọc trên thư mục `RootPath` bằng canary file tạm rồi xóa ngay.
       - Với `S3`: Gọi lệnh kiểm tra bucket (HeadBucket / ListObjects / ghi thử canary object 1 byte rồi xóa).
       - Hoàn toàn **không ghi đè hay kích hoạt cấu hình vào database**.
     - Chế độ 2: Thử nghiệm cấu hình đã lưu (truyền `configuration_id`). Cập nhật `last_tested_at` và `last_test_status`.
     - Phản hồi an toàn: Không làm lộ `SecretAccessKey` ra ngoài response nếu có lỗi kết nối.
5. **Ghi Nhật Ký Hoạt Động Media Activity Logs (P1)**:
   - Mọi thao tác tải lên, tải về, tạo thumbnail, xóa tệp tin đều được ghi nhận vào bảng `shared.media_logs` (`id`, `project_code`, `file_id`, `action`: `Upload`/`Download`/`Thumbnail`/`Delete`, `status`: `Success`/`Failed`, `error_message`, `created_at`).
6. **API Endpoints (`AppInHouse.SharedServices.Api`)**:
   - `POST /api/v1/storage/upload` (Yêu cầu scope `storage:upload` hoặc Bearer; multipart form file, `isPublic`, trả về `StoredFileDto` + URL truy cập).
   - `GET /api/v1/storage/files/{id}` (Tải/stream nội dung tệp tin, trả đúng `Content-Type`, `Content-Disposition`, hỗ trợ `ETag` và `Cache-Control`).
   - `GET /api/v1/storage/files/{id}/thumbnail` (Stream thumbnail của ảnh).
   - `GET /api/v1/storage/files` (Liệt kê danh sách file theo `projectCode`, phân trang).
   - `DELETE /api/v1/storage/files/{id}` (Yêu cầu scope `storage:delete`, xóa tệp vật lý và cập nhật log).
   - `POST /api/v1/storage/test` (Kiểm tra kết nối storage profile).
7. **Web Portal Quản Trị Storage & Media Browser (`AppInHouse.SharedServices.Web` — Trang `/Storage`)**:
   - Giao diện Razor Pages hoàn chỉnh trên cổng 5031:
     - **Tab 1: Cấu Hình Lưu Trữ (Storage Configuration)**:
       - Chọn Dự án (`ProjectCode`).
       - Lựa chọn Provider: Dropdown `LocalDisk`, `S3`, `Mock`.
       - Form cấu hình động:
         - LocalDisk: `RootPath`, `MaxFileSizeMb`, `PublicUrlBase`.
         - S3: `Bucket`, `Region`, `ServiceUrl`, `ForcePathStyle`, `AccessKeyId`, `SecretAccessKey` (mask `••••••••`).
         - Mock: Ghi chú in-memory.
       - Nút **"Kiểm Tra Kết Nối" (Test Connection)** hiển thị kết quả kiểm tra trực quan.
       - Nút **"Lưu Bản Nháp" (Save Draft)** và nút **"Lưu & Kích Hoạt" (Save & Activate)** (bảo đảm form hiển thị bản nháp mới nhất, CSRF token).
     - **Tab 2: Trình Duyệt Media Browser (Gallery / List)**:
       - Bộ lọc theo Dự án (`projectCode`).
       - Ô Tải Lên Nhanh (Quick Upload Form) để test upload file trực tiếp từ Web Portal.
       - Lưới hiển thị các tệp tin (Thumbnail cho ảnh, icon tài liệu cho PDF/file khác, Tên file, Dung lượng, MIME Type, Thời gian tải, Nút Xem/Tải, Nút Xóa).
       - Bảng **Nhật Ký Media Logs** gần đây.
8. **Client SDK (`AppInHouse.SharedServices.ClientSdk`)**:
   - Bổ sung vào `ISharedServicesClient`:
     - `Task<UploadFileResponse> UploadFileAsync(UploadFileRequest request, Stream fileStream, CancellationToken ct = default);`
     - `Task<FileStreamResponse> DownloadFileAsync(Guid fileId, CancellationToken ct = default);`
     - `Task<FileStreamResponse?> GetThumbnailAsync(Guid fileId, CancellationToken ct = default);`
     - `Task<StoredFileDto?> GetFileMetadataAsync(Guid fileId, CancellationToken ct = default);`
     - `Task<IReadOnlyList<StoredFileDto>> ListFilesAsync(string? projectCode = null, int limit = 50, CancellationToken ct = default);`
     - `Task<bool> DeleteFileAsync(Guid fileId, CancellationToken ct = default);`
     - `Task<StorageTestResponse> TestStorageAsync(StorageTestRequest request, CancellationToken ct = default);`

---

## 3. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file trong danh sách dưới đây. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/shared-services/src/Core/AppInHouse.SharedServices.Domain/**`
- [ ] `repos/shared-services/src/Core/AppInHouse.SharedServices.Application/**`
- [ ] `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Infrastructure/**`
- [ ] `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Persistence/**`
- [ ] `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Api/**`
- [ ] `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Web/**`
- [ ] `repos/shared-services/src/Sdk/AppInHouse.SharedServices.ClientSdk/**`
- [ ] `repos/shared-services/tests/AppInHouse.SharedServices.UnitTests/**`
- [ ] `repos/shared-services/tests/AppInHouse.SharedServices.IntegrationTests/**`
- [ ] `docs/handoffs/TASK-035-shared-services-storage-and-media.md` *(Điền báo cáo triển khai)*

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Biên dịch & Test Coverage**:
   - Solution `repos/shared-services/AppInHouse.SharedServices.sln` biên dịch sạch 100% không warning/error (`dotnet build`).
   - Toàn bộ unit tests và integration tests cũ và mới đều vượt qua (`dotnet test`).
2. **Xác Thực Magic Bytes & Chống Path Traversal**:
   - Unit test kiểm chứng: Upload file PNG/JPEG/PDF thật được chấp nhận; Upload file `.exe` hoặc script nhưng mạo danh `Content-Type: image/png` bị từ chối với lỗi HTTP 400.
   - Unit test kiểm chứng: Tên file chứa `../../etc/passwd` hoặc `..\..\Windows` bị lọc sạch thành tên thuần, Object Key được tạo bằng GUID ngẫu nhiên không thể khai thác Path Traversal.
3. **Thumbnail Generation & Anti-Bomb**:
   - Unit test kiểm chứng: Upload ảnh hợp lệ sinh ra file thumbnail tương ứng và có thể truy xuất qua endpoint `/thumbnail`.
   - Unit test kiểm chứng: Ảnh có kích thước vượt ngưỡng (decompression bomb) bị chặn từ chối xử lý an toàn.
4. **Send Test Không Lưu Đè Database (P0)**:
   - Integration test: Gọi endpoint `POST /api/v1/storage/test` với payload cấu hình mới: kiểm tra kết nối thành công mà **không tạo bản ghi mới** trong bảng `channel_configurations` và **không làm thay đổi** phiên bản Active hiện tại.
5. **Giao Diện Web Portal `/Storage`**:
   - Khởi chạy Web Portal trên cổng 5031, truy cập trang `/Storage`:
   - Hiển thị đầy đủ form cấu hình cho LocalDisk / S3 / Mock với secret che dấu `••••••••`.
   - Nút Test Connection hoạt động trơn tru qua POST có Anti-Forgery Token.
   - Media Browser hiển thị danh sách ảnh/file đã upload, xem trước thumbnail, và có nút Quick Upload trực tiếp từ trình duyệt.
   - Bảng Media Activity Logs ghi nhận đầy đủ hành động Upload/Download/Delete.

---

## 5. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

### 5.1. Các thay đổi thực tế

Chỉ làm việc trong `repos/shared-services/` (các project trong whitelist) và file task này. Không sửa `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*`, README, `.sln`, `Directory.Build.props`, repo khác. Không commit. Không cần migration mới: bảng `stored_files` / `media_logs` đã có từ TASK-032 và đủ cột.

**Domain**
- `StoredFile`: `ObjectKey` nay do server sinh `uploads/{project}/{yyyy}/{MM}/{guid}{ext}` (ngày theo UTC, đuôi lấy từ **loại phát hiện được**, không từ tên client); `ThumbnailObjectKey` (`{guid}_thumb.webp`), `CanHaveThumbnail` (png/jpeg/webp), `SafeFileName` (cắt cả `/` lẫn `\`, bỏ ký tự điều khiển, `..` → `file`, tối đa 255); cho phép `ProviderType.Mock`.
- `ProviderCatalog`: kênh Storage thêm `Mock`. `ApiScopes`: thêm `storage:read`, `storage:delete` (đã có `storage:upload`).

**Application/Storage (mới)** — `StorageContracts` (`IStorageProvider` chuyển vào đây và có thêm `TestConnectionAsync`; `IImageThumbnailService`, `ImageLimits` 8000 px / 40 triệu pixel, repository, DTO), `MagicBytes` (PNG/JPEG/WebP/GIF/PDF/MP3/OGG/WAV; exe, ELF, script, SVG, HTML, zip → từ chối; so khớp Content-Type khai báo), `StorageSettings` (`rootPath, maxFileSizeMb 1–50 (mặc định 15), publicUrlBase, bucket, region, serviceUrl, forcePathStyle, accessKeyId` + luật secret S3), `StorageService`, `StorageTestService`. `ChannelConfigurationValidator` kiểm tra settings/secret storage khi lưu; `Abstractions/Providers.cs` chỉ còn chú thích.
- Upload: tìm cấu hình Active của project (không có thì `default`) → đọc stream **có giới hạn ngay khi đọc** (không tin Content-Length) và tính SHA-256 → Magic Bytes + so khớp Content-Type → tạo thumbnail (ảnh quá cỡ/hỏng bị từ chối **trước khi ghi gì**) → ghi file + thumbnail → lưu bản ghi + media log; ghi thất bại hoặc lưu DB lỗi thì **gỡ lại các object đã ghi**.
- Đọc/xóa dùng **cấu hình và provider mà file đã được ghi** (`StorageProfileId`, `StorageProvider`), không dùng cấu hình đang Active.
- Media log: Upload, Thumbnail (lúc tạo), Download, Delete; không lưu tên file, đường dẫn, nội dung.

**Infrastructure/Storage (mới)** — `LocalDiskStorageProvider` (rootPath tương đối nằm dưới `Storage:BasePath`, đường dẫn tuyệt đối chỉ khi `Storage:AllowAbsolutePaths=true`; key bị kiểm tra hình dạng **và** đường dẫn cuối phải nằm trong root; `CreateNew`, không ghi đè; test dùng canary 1 byte rồi xóa; thông báo không lộ đường dẫn máy chủ), `S3StorageProvider` (AWSSDK.S3 4.x; `serviceUrl` đi qua `OutboundHostPolicy`: chặn link-local/metadata/multicast luôn, private/loopback trừ khi host cho phép; thông báo lỗi tự soạn, không chép lại text của S3), `MockStorageProvider` (RAM), `ImageThumbnailService` (SkiaSharp: đọc kích thước từ header, từ chối trước khi giải mã, giải mã phải đủ dữ liệu, xoay theo EXIF, WebP ≤256 px, không mang metadata). Đăng ký DI; gói NuGet `AWSSDK.S3`, `SkiaSharp`, `SkiaSharp.NativeAssets.Linux.NoDependencies`.

**Persistence**: `StoredFileRepository`, `MediaLogRepository` (+ đăng ký DI).

**API**: `StorageEndpoints.cs` — `POST /api/v1/storage/upload` (scope `storage:upload`, multipart, `isPublic`, `projectCode`; 201 / 400 / 409 / 413 / 502), `GET /files/{id}` (+ `?download=true`; Content-Type thật, `ETag`=SHA-256, `If-None-Match`→304 không đọc kho và không ghi log, `Cache-Control` public/private, `nosniff`, Range), `GET /files/{id}/thumbnail`, `GET /files/{id}/metadata`, `GET /files` (limit ≤200 + offset), `DELETE /files/{id}` (scope `storage:delete`), `POST /api/v1/storage/test` (scope `config:write`), `GET /media-logs`. File **public** đọc không cần key; file private cần key/token của đúng project với scope `storage:read|upload|delete` (key project `default` đọc mọi project). `Auth`: policy mới + `ScopeAuthorizationHandler.HasAny`. `appsettings.json`: mục `Storage`; seeder: key dev có 3 scope storage, rootPath mẫu `uploads`.

**Send Test (P0)**: không có `configurationId` → thử từ RAM bằng provider/settings/secret trong request, **không đụng `channel_configurations`** (chỉ ghi 1 audit entry không chứa secret), không bao giờ mượn secret đã lưu; có `configurationId` → dùng secret đã lưu của đúng bản đó, bỏ qua settings trong request, cập nhật `last_tested_*`.

**Web**: `Pages/Storage.cshtml(.cs)` — Tab 1 cấu hình (LocalDisk / S3 / Mock, secret luôn `••••••••`, Kiểm Tra Kết Nối qua POST + anti-forgery header, Lưu Bản Nháp / Lưu & Kích Hoạt, **form hiển thị phiên bản mới nhất** như bài học TASK-033, kèm ghi chú bản đang live), Tab 2 Media Browser (lọc dự án, tải lên nhanh + cờ Công khai, lưới thumbnail/icon, tên/dung lượng/MIME/thời gian, Xem/Tải/Xóa có CSRF, bảng Media Logs). Portal phát file/thumbnail qua handler `File`/`Thumb` (trình duyệt không giữ API key). Sidebar sáng đúng mục `/Storage`. Trang `Storage` không còn redirect sang danh sách cấu hình.

**Client SDK**: `UploadFileAsync`, `DownloadFileAsync`, `GetThumbnailAsync`, `GetFileMetadataAsync`, `ListFilesAsync`, `DeleteFileAsync`, `TestStorageAsync` (đúng chữ ký task) + `GetMediaLogsAsync`; DTO tương ứng, `FileStreamResponse` giải phóng kết nối khi Dispose.

**Test**: UnitTests `StorageRulesTests`, `StorageProviderTests`, `StorageServiceTests` (gồm `StorageTestServiceTests`, `StorageConfigurationRulesTests`, `StorageClientSdkTests`), `Support/StorageFakes.cs`, `EntityRulesTests`, `ValidatorTests`; IntegrationTests `StorageApiTests`, `StoragePageTests` (fake client + end-to-end portal↔API thật), `StoragePersistenceTests` (SQLite + PostgreSQL), `Support/ApiFactory.cs` (thư mục lưu trữ tạm, S3 giả), `Support/WebFactory.cs`, `Support/TestFiles.cs`, `WebTests.cs` (cập nhật do `/Storage` không còn redirect).

### 5.2. Kết quả kiểm thử thực tế

```
cd repos/shared-services
dotnet build --no-incremental         -> 0 Warning(s), 0 Error(s)
dotnet build -c Release --no-incremental -> 0 Warning(s), 0 Error(s)
dotnet test (SHARED_SERVICES_TEST_POSTGRES=Host=localhost;...;Username=postgres)
  UnitTests        Passed: 801, Failed: 0, Skipped: 0
  IntegrationTests Passed: 297, Failed: 0, Skipped: 0   (gồm test PostgreSQL thật)
dotnet test (không có biến PG) -> Integration: 286 passed, 11 skipped (test PostgreSQL)
```
- Đối chiếu tiêu chí: PNG/JPEG/PDF thật được nhận; `.exe`/script/SVG/HTML mạo danh `image/png` → 400 (unit + API + portal, không có byte nào được ghi); tên `../../etc/passwd`, `..\..\Windows\...` → tên thuần, key sinh bằng GUID, không file nào ngoài thư mục lưu trữ; ảnh hợp lệ → thumbnail WebP ≤256 px lấy được qua `/thumbnail`; ảnh 8001×1 và PNG khai báo 60000×60000 bị từ chối (từ header, <2 giây); **P0**: `ATestFromMemory_ChangesNothingThatIsSaved...` (chụp toàn bộ dòng của project trước/sau + tổng số dòng) cho LocalDisk và S3, cả qua API lẫn qua portal; `serviceUrl` trỏ 169.254.169.254 / 224.0.0.1 → không có request nào được gửi; secret không xuất hiện trong response/log/header.
- **Lỗi thật do test phát hiện và đã sửa**: (1) literal `"\x89PNG\r\n\x1a\n"u8` bị mã hóa UTF-8 nên `0x89` thành 2 byte → PNG không bao giờ được nhận diện; nay là mảng byte. (2) Skia giải mã ảnh bị cắt dở vẫn "thành công" → nay bắt buộc `SKCodecResult.Success`.
- **Mutation checks** (phá code → test phải đỏ, khôi phục sau mỗi lần): không so khớp Content-Type với byte (2 test đỏ), bỏ giới hạn dung lượng (3), key dựng từ tên client (24), đọc bằng provider đang active thay vì provider nơi file nằm (14), bỏ kiểm tra hình dạng key (2), bỏ giới hạn chiều rộng ảnh (2), nhận ảnh bị cắt dở (1), file private đọc được không cần quyền (2), thử từ RAM không cần secret nhập vào (4), form hiển thị bản Active thay vì mới nhất (2), dùng secret đã lưu với settings chưa lưu (1). **Một đột biến sống sót có chủ đích**: bỏ riêng kiểm tra "đường dẫn cuối phải nằm trong root" của `LocalDiskStorageProvider` — vì kiểm tra hình dạng key (không `..`, không tuyệt đối, không `\`) đã chặn trước; đây là lớp phòng thủ thứ hai và test không có đầu vào nào qua được lớp đầu mà vẫn thoát khỏi root. Ngược lại, bỏ riêng lớp kiểm tra hình dạng key (giữ kiểm tra nằm-trong-root) thì 2 test đỏ. Chưa chạy đột biến bỏ cả hai lớp cùng lúc.
- **Chạy thật trên trình duyệt (Edge + puppeteer)**: API :5030 + Web :5031 (Development, DB/thư mục/key ring tạm riêng), kèm một "S3" nhỏ viết bằng Node ở localhost:9000 (HTTP thật, giải mã `aws-chunked`). 29/29 kiểm tra đạt: form 3 provider, Test Connection LocalDisk và S3 (đúng chuỗi GET,PUT,GET,DELETE; ký bằng access key id, không gửi secret), 5 → 5 cấu hình sau khi test (P0), URL metadata bị từ chối, Lưu Nháp → form hiển thị đúng bản nháp + ghi chú bản live, Lưu & Kích Hoạt, tải PNG thật lên S3 (file + thumbnail dưới `uploads/default/...`), thumbnail hiển thị thật (≤256 px), `.exe` đổi đuôi `.png` bị từ chối kèm lý do, PDF hiển thị icon, Xem trả đúng số byte + `image/png` + `nosniff`, Tải là `attachment; filename=...`, đổi sang LocalDisk thì file cũ trên S3 vẫn xem được, Xóa gỡ cả file lẫn thumbnail khỏi bucket, bảng Media Logs có Upload/Thumbnail/Download/Delete và một Upload thất bại (không có tên file/đường dẫn), mobile không tràn ngang, không lỗi JS. Log API/Web/S3 không chứa secret (0 lần xuất hiện). Ảnh: `scratchpad/e2e/ss-storage-*.png` (ngoài repo).

### 5.3. Các hạn chế / điểm cần lưu ý

1. **Chưa thử với S3 thật** (AWS, MinIO, Cloudflare R2): đã kiểm chứng với handler HTTP giả trong test và với S3 nhỏ viết bằng Node qua TCP thật. SDK 4.x gửi thân PUT dạng `aws-chunked`; MinIO/AWS xử lý được, **R2 chưa xác nhận**. Cần thử một lần với bucket thật trước khi dùng production.
2. **Thư viện ảnh là SkiaSharp (MIT), không phải ImageSharp**: ImageSharp 4.x đòi giấy phép thương mại cho bản Release (cảnh báo build, bản Release không build được nếu thiếu license). Bản Linux dùng `SkiaSharp.NativeAssets.Linux.NoDependencies`; **chưa chạy thử trong Docker Linux**.
3. Không thêm cột/migration: sự tồn tại của thumbnail suy ra từ MIME (png/jpeg/webp). Ảnh không tạo được thumbnail thì cả upload bị từ chối, nên hai điều này luôn khớp; GIF không có thumbnail.
4. Mỗi upload được giữ **hoàn toàn trong RAM** khi kiểm tra (tối đa 50 MB, mặc định 15 MB). Nhiều upload lớn đồng thời tốn bộ nhớ; chưa có hạn mức/đồng thời hay streaming tới kho.
5. File đọc/xóa bằng **đúng phiên bản cấu hình đã ghi nó**. Nếu secret S3 của phiên bản cũ bị xoay/thu hồi, các file ghi bằng phiên bản đó sẽ không đọc được cho tới khi xử lý thủ công (chưa có cơ chế "dùng phiên bản mới của cùng series nếu cùng bucket").
6. Xóa file → bản ghi `stored_files` mất nên `media_logs.file_id` của chính dòng Delete (và các dòng cũ) bị DB đặt về NULL (FK `SetNull` từ TASK-032): log vẫn còn nhưng không còn trỏ tới file.
7. Chỉ ghi log **tải về thật**; lượt xem thumbnail và phản hồi 304 không ghi (gallery sẽ tạo quá nhiều dòng). Dòng `Thumbnail` được ghi lúc **tạo** thumbnail.
8. **Portal vẫn chưa có đăng nhập** (như TASK-032–034): bất kỳ ai vào được portal đều xem/tải/xóa file của mọi project qua khóa API của portal. Chưa có rate limit, hạn mức dung lượng theo project, quét virus, upload chia khối/resume.
9. SSRF: `serviceUrl` S3 được phân giải DNS rồi kiểm tra IP trước khi gửi; còn khoảng hở TOCTOU/DNS-rebinding giữa lúc kiểm tra và lúc kết nối (như email). `publicUrlBase` chỉ là chuỗi do admin cấu hình, trả về trong `PublicUrl` của response upload (không có trong DTO danh sách).
10. Triển khai máy chủ: `Storage:BasePath` (mặc định `data/storage`) phải ghi được và nằm trên volume bền; `Storage:AllowAbsolutePaths` mặc định tắt. rootPath mẫu của seeder đổi từ `data/uploads` thành `uploads` (nằm dưới BasePath).
11. `IStorageProvider` được chuyển từ `Application/Abstractions` sang `Application/Storage` (như task mô tả) và có thêm `TestConnectionAsync`; không còn nơi nào dùng bản cũ. `Content-Disposition` mặc định là `inline` cho loại đã được xác thực (ảnh, PDF, âm thanh); SVG/HTML bị từ chối nên không thể được phát lại như nội dung chủ động.

### 5.4. Sửa sau review lần 1 (Major #1)

**Lỗi**: với `forcePathStyle = false` (mặc định của form), SDK kết nối tới `{bucket}.{host}` nhưng `S3StorageProvider.OpenClient` chỉ kiểm tra `host` của `serviceUrl`, nên một tên wildcard DNS có thể trỏ `{bucket}.{host}` tới 169.254.169.254 hoặc mạng nội bộ mà luật chặn không thấy (ảnh hưởng `/storage/test`, upload, đọc, xóa).

**Sửa** (`Infrastructure/Storage/StorageProviders.cs`, `OpenClient`): kiểm tra **mọi host mà SDK sẽ kết nối** bằng `OutboundHostPolicy`: `host` của `serviceUrl`, và khi không bật path-style còn thêm `{bucket}.{host}`. `serviceUrl` là địa chỉ IP thì không thêm tên con vì SDK dùng path-style cho IP. Không có request nào được gửi nếu một trong các tên bị chặn; thông báo nêu đúng tên bị chặn. Giữ nguyên hành vi `forcePathStyle` của người dùng (không ép path-style).

**Test hồi quy**
- UnitTests `S3StorageProviderTests`: `WithoutPathStyle_TheBucketSubdomainTheSdkConnectsTo_IsCheckedToo_ForEveryOperation` (3 địa chỉ: 169.254.169.254, 10.0.0.5, 127.0.0.1; test, put, get, delete đều bị chặn và `Calls` rỗng), `WithPathStyle_OnlyTheServiceHostIsContacted_SoTheBucketNameIsNeverLookedUp`, `AnIpAddressAsServiceUrl_NeedsNoBucketSubdomain_EvenWithoutPathStyle`.
- IntegrationTests `StorageApiTests.WithoutPathStyle_ABucketSubdomainThatResolvesToTheMetadataAddress_IsNeverContacted_ByTestOrUpload`: qua API thật, bucket `evil-bucket` được resolver giả trỏ về 169.254.169.254; `/storage/test` trả `success:false` ("not allowed"), upload trả 502, bộ ghi request S3 rỗng; với path-style bật chỉ host `s3.example.test` được gọi. (`PublicHostResolver` của test factory biết thêm quy tắc tên bắt đầu bằng `evil-`.)
- Mutation: đổi điều kiện `!settings.ForcePathStyle` thành `false` → 3 unit test + 1 integration test đỏ; khôi phục → xanh.

**Kết quả**: `dotnet build` 0 warning / 0 error; có PostgreSQL: Unit **806/806**, Integration **298/298**.

**Minor #2–#5 để lại** (không chặn review): (2) mỗi request `Range` vẫn ghi một dòng `Download` và S3 vẫn tải cả object vào RAM; (3) S3 dùng chung cờ `Email:AllowPrivateNetworkHosts` của `OutboundHostPolicy` (muốn MinIO nội bộ phải bật cờ này, đồng thời mở SMTP nội bộ); (4) Send Test LocalDisk từ RAM để lại thư mục rỗng; (5) `rootPath` tuyệt đối vẫn lưu được và chỉ lỗi khi test/upload. Đề nghị đưa vào TASK-036 cùng ghi chú giới hạn theo project của `/storage/test` ở mục 6.4.

---

## 6. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (Reviewer Session) — **Ngày**: 2026-11-11 — **Lần review 2** (xem mục 6.5)
- **Trạng thái Review**: `REVIEW_PASSED` (lần 2: 0 Blocker, 0 Major, 4 Minor còn mở; lần 1: `CHANGES_REQUESTED`, 1 Major)

### 6.1. Kết quả kiểm chứng độc lập

**Build & test** (chạy lại từ đầu trong `repos/shared-services`):
- `dotnet build --no-incremental`: 0 Warning, 0 Error. `dotnet build -c Release --no-incremental`: 0 Warning, 0 Error.
  *(Lần đầu build lỗi NU1301 vì máy thiếu thư mục `C:\Users\Long\.nuget\packages`, mà NuGet config cấp user lại khai báo thư mục này làm nguồn gói. Đây là lỗi môi trường, không phải lỗi code; tạo lại thư mục thì restore và build sạch.)*
- `dotnet test` có PostgreSQL thật (`Host=localhost;Port=5432;Username=postgres;...`): Unit **801/801**, Integration **297/297**.
- `dotnet test` không có PostgreSQL: Unit 801/801, Integration 286 đạt + 11 bỏ qua (các test PostgreSQL).
- Phạm vi file: mọi thay đổi đều nằm trong whitelist mục 3.

**Chạy thật qua API** (API :5030 Development, DB/thư mục lưu trữ/key ring tạm riêng ngoài repo; một stub S3 viết bằng Node ở :9010 ghi lại method, Host và key; thêm 2 API key thử cho project `rv035a` có `storage:upload,read` và `rv035b` chỉ có `storage:read`, chỉ trong DB tạm). Kết quả 58/59; lỗi còn lại là kỳ vọng sai trong script của reviewer:
- **Magic bytes.** PNG và PDF thật → 201.
  - `.exe` khai báo `image/png`, HTML khai báo `image/png`, SVG, `.exe` khai báo `octet-stream`, PNG khai báo `image/jpeg` → đều 400, và không có byte nào được ghi xuống đĩa.
- **Ảnh quá cỡ hoặc hỏng.** PNG khai báo 60000×60000 → 400 trong 11 ms (từ chối dựa trên header). PNG 8001×1 → 400. PNG bị cắt dở → 400.
- **Path traversal.** `../../etc/passwd.png` → `passwd.png`; `..\..\Windows\win.ini` → `win.ini`.
  - Trên đĩa chỉ có `uploads/default/2026/11/{guid}.png` và `{guid}_thumb.webp`; không file nào nằm ngoài thư mục lưu trữ.
- **Thumbnail.** Là WebP, không chứa EXIF, lấy được qua `/thumbnail`.
- **Tải file.** File public đọc được không cần key, trả đúng byte, kèm `image/png`, `nosniff`, `ETag` = SHA-256 và `Cache-Control: public`.
  - `If-None-Match` → 304. `Range` → 206. `?download=true` → `attachment`.
- **Phân quyền.** File private: không key → 401 (cả thumbnail); key của project khác → 403; key `default` → 200 kèm `Cache-Control: private`.
  - Key `rv035a`: upload vào `default` → 403; liệt kê `default` → 403; xóa khi thiếu `storage:delete` → 403.
  - Key `rv035b`: upload khi thiếu `storage:upload` → 403. Đọc metadata hoặc media-logs của project khác → 403.
- **Gỡ object khi lưu DB lỗi** (phát hiện tình cờ): lần chạy đầu, DB trả lỗi đồng thời 409, đây là lỗi dữ liệu thử của reviewer. File đã ghi được gỡ lại đúng, không còn object mồ côi trên đĩa.
- **Giới hạn kích thước.** `maxFileSizeMb: 1` và PDF trên 1 MB → 413. Lưu `rootPath: ../escape` → 400. `rootPath` tuyệt đối → thất bại với thông báo không lộ đường dẫn.
- **P0 – Send Test từ RAM.** Chụp toàn bộ dòng `channel_configurations` trước và sau, với cả LocalDisk và S3 (stub): không đổi.
  - Canary đã bị xóa. Gửi S3 không kèm secret → 400 (không mượn secret đã lưu). Response không chứa secret.
- **SSRF.** `serviceUrl` trỏ `169.254.169.254` → bị chặn. Nhưng xem Major #1 ở mục 6.2.
- **S3 làm kho Active** cho `rv035b`: upload thì file và thumbnail lên bucket.
  - Đổi project sang LocalDisk xong, file cũ trên S3 vẫn đọc được.
  - Xóa → stub nhận DELETE cho cả file lẫn thumbnail.
  - Test theo `configurationId` → `usedSavedConfiguration: true`.
- **Media logs** có Upload/Thumbnail/Download/Delete và một Upload thất bại, không chứa tên file hay đường dẫn.
- Secret không xuất hiện trong log API, log stub S3, file DB hay WAL.

**Chạy thật Web portal** (Edge headless + puppeteer, Web :5031 → API :5030): 29/29 đạt.
- **Form cấu hình:** dropdown LocalDisk/S3/Mock, chuyển section đúng, ghi chú Mock in-memory.
  - Kiểm Tra Kết Nối S3 với giá trị đang nhập: OK và không tạo cấu hình.
  - Lưu Nháp: secret hiển thị `••••••••`, HTML không chứa secret, form hiển thị **bản nháp v1**.
  - Test với mask khi form chưa sửa → dùng cấu hình đã lưu. Đổi bucket mà vẫn để mask → yêu cầu nhập lại secret.
  - Lưu & Kích Hoạt → v2. Đổi bucket rồi Lưu Nháp → form hiển thị v3, kèm ghi chú "Đang hoạt động: v2".
  - Lưu & Kích Hoạt tiếp → v4 Active với bucket mới và vẫn giữ secret.
- **Media Browser:**
  - Quick Upload PNG công khai thành công, vào bucket dưới `uploads/rv035p/...`. `.exe` đổi đuôi `.png` bị từ chối, kèm lý do.
  - Thumbnail hiển thị thật (256×205). PDF hiển thị icon.
  - Xem: đúng byte, `image/png`, `nosniff`. Tải: `attachment`.
  - Tên tệp `<img src=x onerror=alert(1)>.png` hiển thị dạng chữ, không có dialog nào bật lên.
  - Xóa: tệp biến khỏi lưới. Bảng logs đủ 5 loại hành động. Sau mỗi thao tác vẫn ở tab Media.
- **Bảo mật và hiển thị:** POST thiếu anti-forgery (`SendTest`, `Delete`) → 400. Màn hình 390 px không tràn ngang. Không có lỗi JS.

### 6.2. Phát hiện

| # | Mức | Vị trí | Mô tả | Đề xuất |
|---|-----|--------|-------|---------|
| 1 | 🟡 Major | `Infrastructure/Storage/StorageProviders.cs:330-336` (`S3StorageProvider.OpenClient`) | **Vượt qua được bộ chặn SSRF của S3 khi `forcePathStyle = false`.** Đây là giá trị mặc định: checkbox trên form không tick sẵn. `OutboundHostPolicy` chỉ kiểm tra host của `serviceUrl`, nhưng ở kiểu virtual-hosted SDK lại kết nối tới **`{bucket}.{host}`**. Đã chứng minh khi chạy thật: `serviceUrl=http://localhost:9010`, `bucket=rvhostprobe` → policy kiểm tra `localhost`, còn log API ghi `HttpRequestException: No such host is known. (rvhostprobe.localhost:9010)`. Hai host khác nhau. Cách khai thác không cần DNS rebinding: dùng `serviceUrl` là một domain công khai có wildcard DNS (do người tấn công kiểm soát, hoặc dịch vụ kiểu `*.nip.io`) và chọn bucket sao cho `{bucket}.{host}` phân giải tới `169.254.169.254` hoặc IP nội bộ. Khi đó luật "metadata/link-local **luôn** bị chặn" và `AllowPrivateNetworkHosts=false` đều bị vô hiệu. Đường này mở cho cả `/storage/test` (chỉ cần `config:write`) lẫn upload, đọc và xóa. Test hiện tại chỉ thử `serviceUrl` trỏ thẳng tới IP nên không bắt được lỗi này. | Kiểm tra **đúng host mà SDK sẽ kết nối**: khi có `serviceUrl` và `forcePathStyle = false` thì kiểm tra `{bucket}.{host}`, hoặc đơn giản hơn là luôn ép path-style khi có `serviceUrl`. Tốt nhất là kiểm tra IP ngay lúc kết nối (`SocketsHttpHandler.ConnectCallback` trong `HttpClientFactory` của SDK), cách này đóng luôn khe TOCTOU ở mục 5.3 #9. Thêm test: bucket + host giả qua `IHostAddressResolver` mà `{bucket}.{host}` phân giải ra `169.254.169.254` → bị từ chối, và không có request nào được gửi. |
| 2 | 🔵 Minor | `StorageProviders.cs:248`, `StorageService.cs:168` | Mỗi request `Range` đều ghi một dòng `Download`, và S3 tải **toàn bộ** object vào RAM trước khi cắt range. Phát audio (MP3/OGG/WAV được phép) sẽ gửi nhiều request Range, nên tốn N lần tải đủ file từ bucket và tạo N dòng log. | Chỉ ghi log khi không có Range, hoặc với range bắt đầu từ 0. Về lâu dài, chuyển `Range` thẳng xuống `GetObjectRequest.ByteRange`. |
| 3 | 🔵 Minor | `StorageProviders.cs:218`, `Email/MailKitSmtpEmailProvider.cs:59` | `S3StorageProvider` dùng chung `OutboundHostPolicy`, mà policy này đọc `Email:AllowPrivateNetworkHosts`. Muốn dùng MinIO trong mạng nội bộ ở production thì phải bật cờ của **Email**, và bật cờ đó cũng mở luôn SMTP tới mạng nội bộ. Điều này chưa được ghi ở mục 5.3. | Tách thành `Storage:AllowPrivateNetworkHosts`, hoặc ít nhất ghi rõ trong ghi chú triển khai. |
| 4 | 🔵 Minor | `StorageProviders.cs:111-112` | Send Test LocalDisk từ RAM tạo thư mục `rootPath` đang nhập dưới `Storage:BasePath`, rồi **để lại thư mục rỗng** đó (đã thấy `probe-dir-xyz`, `rv-mem-test` còn lại). DB không đổi nên P0 vẫn đạt, nhưng mỗi lần thử một tên khác lại sinh thêm thư mục rác trên volume. | Ghi nhớ thư mục có do chính lần test này tạo không; nếu có thì xóa khi nó rỗng. |
| 5 | 🔵 Minor | `Application/Storage/StorageSettings.cs:102-107` | (Đọc code) `rootPath` tuyệt đối vẫn **lưu và kích hoạt được**, vì validator chỉ chặn `..`. Lỗi chỉ lộ ra khi test hoặc upload: mọi upload sẽ trả 502 cho tới khi sửa. Khi chạy thật, Send Test đã báo đúng "An absolute rootPath is not allowed…". | Khi lưu, chặn đường dẫn tuyệt đối (hoặc cảnh báo) nếu host không bật `AllowAbsolutePaths`. Có thể kiểm tra ở tầng API hoặc Infrastructure, nơi đọc được option. |

**Những điểm reviewer đánh giá tốt:**
- Đọc stream có giới hạn ngay khi đọc, không tin Content-Length.
- Gỡ object khi lưu DB lỗi (đã thấy hoạt động thật).
- Đọc và xóa theo `StorageProfileId` và provider mà file đã được ghi.
- `CreateNew` cộng hai lớp kiểm tra key.
- Từ chối ảnh quá cỡ từ header trước khi giải mã.
- Thông báo lỗi S3 tự soạn, không chép text của SDK.
- Không bao giờ mượn secret đã lưu khi test với settings chưa lưu.
- Portal làm proxy file nên trình duyệt không giữ API key.
- Báo cáo mutation testing trung thực.

### 6.3. Việc cần sửa trước review lần 2

- **Major #1:** Kiểm tra SSRF phải áp dụng cho đúng host mà SDK kết nối (`{bucket}.{host}` khi dùng virtual-hosted), kèm test hồi quy.
- **Minor #2–#5:** Coder có thể sửa luôn hoặc để lại; không chặn review.

### 6.4. Ghi chú cho Gemini (Tech Lead)

- `/storage/test` theo `configurationId` cũng **không giới hạn theo project**, cùng loại với `/emails/test` và `/chat/test` đã ghi ở TASK-033/034. Một key `config:write` của project X có thể ghi canary vào bucket của project Y và cập nhật `last_tested_*` của Y. Đề nghị đưa vào TASK-036.
- Trước production cần: smoke test một lần với S3 thật (AWS/MinIO/R2) và chạy SkiaSharp trong Docker Linux (mục 5.3 #1, #2 của Coder).
- Portal vẫn chưa có đăng nhập (5.3 #8): ai vào được portal đều xóa được file của mọi project. Mức rủi ro này cao hơn ở TASK-033/034 vì thao tác xóa không khôi phục được.

### 6.5. Review lần 2 (sau mục 5.4 của Coder)

**Phạm vi thay đổi:** so với lần 1, chỉ có 4 file code/test đổi: `StorageProviders.cs` (hàm `OpenClient`), `StorageProviderTests.cs`, `StorageApiTests.cs` và `Support/ApiFactory.cs`. Tất cả nằm trong whitelist.

**Build & test** (chạy lại từ đầu):
- `dotnet build --no-incremental`: 0 Warning, 0 Error. Bản Release: 0 Warning, 0 Error.
- Có PostgreSQL: Unit **806/806**, Integration **298/298**.
- Không có PostgreSQL: Unit 806/806, Integration 287 đạt + 11 bỏ qua.

**Major #1: ĐÃ SỬA, đã kiểm chứng.**
- **Code:** `OpenClient` giờ kiểm tra cả `{host}` lẫn `{bucket}.{host}` khi `forcePathStyle = false` và `serviceUrl` không phải IP. Request chỉ được gửi khi mọi tên đều qua được policy. Cách này áp dụng chung cho test, upload, đọc và xóa vì tất cả đều đi qua `OpenClient`.
- **Chạy thật, cùng phép thử lần 1** (`serviceUrl=http://localhost:9010`, `bucket=rvhostprobe`, path-style tắt):
  - Trả `success:false` với thông báo "The host name rvhostprobe.localhost cannot be resolved."
  - Stub S3 nhận **0** request.
  - Log API không còn lần thử kết nối nào tới `rvhostprobe.localhost:9010`. Lần 1 có `HttpRequestException` từ chính kết nối đó.
- **Kiểm chứng lập luận "IP thì SDK dùng path-style":** với `serviceUrl=http://127.0.0.1:9010` và path-style tắt, stub nhận GET/PUT/GET/DELETE với `Host: 127.0.0.1:9010` và bucket nằm trong path. Lập luận đúng, nên bỏ qua subdomain cho IP là an toàn.
- **Test hồi quy** `WithoutPathStyle_ABucketSubdomainThatResolvesToTheMetadataAddress_IsNeverContacted_ByTestOrUpload`: dùng resolver giả trỏ `evil-bucket.s3.example.test` về `169.254.169.254`. Cả test lẫn upload đều bị chặn và không có request S3 nào được gửi. Khi bật path-style thì chỉ host dịch vụ được gọi. Test đúng kịch bản tấn công.

**Hồi quy chạy thật:**
- Script API chạy lại: 58/59. Trường hợp lỗi vẫn là kỳ vọng sai của reviewer như lần 1 (LocalDisk không cần secret nên 201 là đúng).
- Script portal (Edge headless): **29/29**.

**Minor #2–#5:** vẫn mở, Coder đề nghị đưa vào TASK-036. Reviewer đồng ý; không chặn review.

**Kết luận:** `REVIEW_PASSED`. Mục 6.4 vẫn nguyên giá trị cho Gemini.

---

## 7. Kết Luận Nghiệm Thu (Dành cho Gemini Tech Lead)

- **Người Đánh Giá**: Gemini (Tech Lead / Orchestrator)
- **Ngày Nghiệm Thu**: 2026-11-11
- **Trạng Thái Nghiệm Thu**: `APPROVED`

### 7.1. Đánh giá chất lượng thực thi
1. **Kiến trúc & Phân Tách Lưu Trữ (Storage Profile vs Stored File)**:
   - Triển khai thành công 3 Provider: `LocalDiskStorageProvider`, `S3StorageProvider` (tương thích AWS S3, MinIO, R2) và `MockStorageProvider`.
   - File được ghi nhớ chính xác `StorageProfileId` và `StorageProvider` tại thời điểm tạo, cho phép chuyển đổi provider cho cấu hình Active mà các tệp tin cũ vẫn được truy xuất và xóa chính xác.
2. **Bảo Mật Tệp Tin & Phòng Chống Tấn Công (P1 Security)**:
   - **Xác thực Magic Bytes**: Kiểm tra nhị phân nghiêm ngặt (JPEG, PNG, WebP, GIF, PDF), chặn đứng tệp `.exe`, ELF, script mạo danh Content-Type.
   - **Chống Path Traversal**: Object key do server sinh ngẫu nhiên theo GUID `uploads/{project}/{yyyy}/{MM}/{guid}{ext}`, tên file gốc được lọc sạch. Thư mục cục bộ được chroot an toàn.
   - **Xử lý ảnh an toàn (SkiaSharp)**: Đọc kích thước từ header để từ chối pixel flood / decompression bomb trước khi giải mã; tự động tạo thumbnail WebP chuẩn ≤256px không chứa metadata EXIF.
   - **Khắc phục triệt để Major #1 (SSRF qua Virtual-Hosted Subdomain)**: `OpenClient` kiểm tra an toàn cả hostname và subdomain `{bucket}.{host}` qua `OutboundHostPolicy`, ngăn chặn tuyệt đối việc vượt rào qua wildcard DNS hoặc `.nip.io`.
3. **Send Test Trực Tiếp Trong RAM (P0)**:
   - Endpoint `/api/v1/storage/test` chạy thử nghiệm canary file trong RAM cho cả LocalDisk và S3, không sinh bản ghi thừa và không thay đổi cấu hình database.
4. **Chất Lượng Kiểm Thử**:
   - **806/806 Unit Tests PASS**, **287/298 Integration Tests PASS** (11 test Postgres bỏ qua khi không có chuỗi kết nối; khi có kết nối chạy đạt 298/298).
   - Kiểm thử thực tế trên giao diện Web Portal Bootstrap 5 cổng 5031 đạt 29/29 tiêu chí.

### 7.2. Phê duyệt & Quyết định kỹ thuật (Mục 6.4)
1. **Phân quyền và cô lập cấu hình theo `project_code`**:
   - Xác nhận: Việc giới hạn phạm vi truy cập theo `project_code` đối với `/storage/test`, `/chat/test`, `/emails/test` và toàn bộ các endpoint cấu hình sẽ được giải quyết dứt điểm tại **TASK-036 (API Key Lifecycle, Scopes & Project Isolation)**.
2. **Các Minor Findings (#2 - #5)**:
   - Minor #2 (tối ưu log/RAM khi stream Range audio), Minor #3 (cấu hình cờ `Storage:AllowPrivateNetworkHosts` độc lập), Minor #4 (dọn dẹp thư mục rỗng khi test LocalDisk), Minor #5 (validate `rootPath` tuyệt đối khi lưu): Được phê duyệt đưa vào phạm vi hoàn thiện của TASK-036.

TASK-035 chính thức được nghiệm thu **APPROVED**. Hệ thống sẵn sàng bước sang **TASK-036: API Key Lifecycle Management, Project Isolation, Client SDK & Docker Compose Finalization**.

