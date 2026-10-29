# TÀI LIỆU THIẾT KẾ KIẾN TRÚC & SẢN PHẨM (PHIÊN BẢN 2.0.0)
# NỀN TẢNG HỌC TIẾNG ANH & TIẾNG TRUNG THỰC CHIẾN (LINGUABRIDGE)

- **Dự án**: **LinguaBridge** (Cầu Nối Ngôn Ngữ Anh – Trung & Giao Tiếp Thương Mại)
- **Tác giả**: Gemini Tech Lead & Solution Architect (Đã cập nhật theo phản biện thực tế)
- **Kiến trúc cốt lõi**: **Modular Monolith** (.NET 10 LTS / Clean Architecture) + Flutter Cross-Platform (Mobile) + Next.js (Web)
- **Phiên bản**: 2.0.0 (Pragmatic Architecture)
- **Ngày cập nhật**: 2026-10-28
- **Trạng thái**: Sẵn sàng triển khai thực tế

---

## 1. TỔNG QUAN SẢN PHẨM & TẦM NHÌN THỰC CHIẾN

### 1.1. Định vị thương hiệu: LinguaBridge
Tên gọi **LinguaBridge** thể hiện vai trò là chiếc cầu nối ngôn ngữ và văn hóa giữa hai cường quốc kinh tế: **Tiếng Anh** và **Tiếng Trung Quốc**. Nền tảng tập trung vào hai nhánh giá trị cốt lõi:
1. **Ngoại ngữ nền tảng & chứng chỉ**:
   - Tiếng Anh: Khung tham chiếu CEFR (A1 -> C1), bài thi định hướng IELTS/TOEIC.
   - Tiếng Trung: Khung chuẩn HSK 3.0 (Cấp 1 -> 6), HSKK, Pinyin và Hán tự (Giản thể làm mặc định, tùy chọn Phồn thể).
2. **Giao tiếp thương mại thực chiến (Business Communication)**:
   - Các tình huống công sở, email đối ngoại, đàm phán thương mại, chuỗi cung ứng, xuất nhập khẩu, phỏng vấn tuyển dụng.

### 1.2. Phân tách rõ 3 lớp ngôn ngữ (Language Triad)
Để tránh nhồi nhét bản dịch vào một trường JSON khổng lồ và tránh nhầm lẫn luồng học:
- **`UiLocale` (Ngôn ngữ hiển thị giao diện)**: `vi-VN`, `en-US`, `zh-CN`, `ja-JP` (Điều khiển text của nút bấm, menu, nhãn hệ thống).
- **`TargetLanguage` (Ngôn ngữ đang học)**: `en` (Tiếng Anh) hoặc `zh` (Tiếng Trung).
- **`BaseLanguage` (Ngôn ngữ mẹ đẻ / giải nghĩa)**: Ngôn ngữ dùng để hiển thị nghĩa từ vựng, ngữ pháp, hướng dẫn bài học (Mặc định `vi` cho người Việt, `en` cho người nước ngoài).

### 1.3. Mô hình kinh doanh thực tế (Monetization)
- **Freemium**: Học các bài cơ bản của lộ trình, 10 lượt tương tác Text AI/ngày, tối đa 20 thẻ SRS mới/ngày.
- **Premium Subscription (Tháng/Năm)**: Không giới hạn bài học, AI Tutor giải thích sâu & sửa câu không giới hạn (có Fair-Use token cap), mở khóa bài luyện nâng cao và đồng bộ đa thiết bị.
- **Khóa học chuyên đề (Pay-per-course)**: Mua đứt từng khóa học chuyên sâu (Tiếng Trung Thương mại, Luyện viết IELTS, ...) kèm bài tập đánh giá.

---

## 2. QUYẾT ĐỊNH KIẾN TRÚC KỸ THUẬT: MODULAR MONOLITH

> [!IMPORTANT]
> **Quyết định cốt lõi**: Loại bỏ hoàn toàn sự phức tạp không cần thiết của Microservices (Service mesh, RabbitMQ, Kafka, nhiều cụm DB, gRPC nội bộ) ở giai đoạn đầu. Thay vào đó, toàn bộ Backend được tổ chức thành **Một Ứng Dụng Đơn Khối Theo Module (Modular Monolith)** trên nền **.NET 10 LTS**.

### 2.1. Sơ đồ kiến trúc tổng thể
```text
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                   CLIENT LAYER                                         │
│   • Flutter Mobile App (iOS / Android)   • Next.js 14 Web Portal (Learner & Studio)    │
└───────────────────────────────────────────┬────────────────────────────────────────────┘
                                            │ HTTPS (RESTful API + SSE + SignalR)
                                            ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                             ASP.NET CORE MODULAR MONOLITH                              │
│   • Auth Integration (OIDC STS)  • Resource-based Auth  • FluentValidation • RateLimit │
├────────────────────────────────────────────────────────────────────────────────────────┤
│  MODULE 1: LEARNING & SRS                                                              │
│  - Courses, Units, Lessons, Questions, Exercise Engine                                 │
│  - SM-2 Spaced Repetition Engine (Review Logs, Due Cards)                              │
│  - User Progress Tracker (Lesson Attempts, Question Responses)                         │
├────────────────────────────────────────────────────────────────────────────────────────┤
│  MODULE 2: AI TUTOR & COACHING                                                         │
│  - Text Tutor (V1), Voice Tutor (V2), Pronunciation (V3)                               │
│  - Prompt Orchestrator, Model Provider Fallback (OpenAI / Azure / Claude)              │
│  - Token Ledger & User Budget Control (Hạn mức chi phí theo ngày/tháng)                │
├────────────────────────────────────────────────────────────────────────────────────────┤
│  MODULE 3: CONTENT AUTHORING & CMS                                                     │
│  - Course Builder, Question Bank, Lesson Lifecycle (Draft -> Published -> Archived)   │
│  - Media Upload & Asset Management                                                     │
├────────────────────────────────────────────────────────────────────────────────────────┤
│  MODULE 4: BILLING & ENTITLEMENTS                                                      │
│  - Plans, Subscriptions, Entitlement Verification                                      │
│  - Idempotent Webhook Handler (VNPay, Stripe, Apple AppStore, Google Play)             │
└───────────────────────────────────────────┬────────────────────────────────────────────┘
                                            │
                                            ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                              SHARED INFRASTRUCTURE LAYER                               │
│   • PostgreSQL (Cơ sở dữ liệu tập trung với Schemas riêng cho từng Module)             │
│   • EF Core 10 (Code-First Migrations, Compiled Queries)                               │
│   • S3-Compatible Object Storage (Cloudflare R2 / MinIO: Media, Audio, HLS)            │
│   • MemoryCache / Redis (Tùy chọn: Chỉ thêm khi có nhu cầu đo được về tải)             │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### 2.2. Lợi ích khi dùng Modular Monolith cho Solo/Lead Developer
- **Phát triển & Debug cực nhanh**: Chỉ cần F5 một process duy nhất để chạy toàn bộ hệ thống; không cần bật 6-7 terminal hay cấu hình Docker Compose phức tạp.
- **Giao tiếp nội bộ in-process (MediatR)**: Các module gọi nhau qua In-Memory Events (`INotification`) mà không có độ trễ mạng hay rủi ro network failure.
- **Dễ dàng tách service trong tương lai**: Vì mỗi module sở hữu riêng Domain Entities, DbContext/Schema và Application Use Cases, khi cần scale một service cụ thể (ví dụ AI Audio Worker), chỉ cần trích xuất folder của module đó thành một service độc lập mà không phải viết lại code.

---

## 3. THIẾT KẾ CƠ SỞ DỮ LIỆU ĐẦY ĐỦ (DATA ARCHITECTURE)

Hệ thống sử dụng **PostgreSQL** với các schema phân định ranh giới module rõ ràng.

### 3.1. Phân hệ Nội dung & Đa ngôn ngữ (Content & Localization)
```sql
CREATE SCHEMA content;

-- Khóa học
CREATE TABLE content.Courses (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    Code VARCHAR(50) UNIQUE NOT NULL,           -- 'EN_CEFR_A1', 'ZH_HSK_1'
    TargetLanguage VARCHAR(10) NOT NULL,        -- 'en', 'zh'
    Level VARCHAR(20) NOT NULL,                 -- 'A1', 'HSK1'
    Status VARCHAR(20) DEFAULT 'Draft',         -- 'Draft', 'Review', 'Published', 'Archived'
    Version INT DEFAULT 1,
    ThumbnailUrl VARCHAR(500),
    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
    UpdatedAt TIMESTAMPTZ DEFAULT NOW()
);

-- Bản dịch thông tin khóa học (Tách biệt theo BaseLanguage)
CREATE TABLE content.CourseTranslations (
    CourseId UUID REFERENCES content.Courses(Id) ON DELETE CASCADE,
    BaseLanguage VARCHAR(10) NOT NULL,          -- 'vi', 'en'
    Title VARCHAR(255) NOT NULL,
    Description TEXT,
    PRIMARY KEY (CourseId, BaseLanguage)
);

-- Bài học cụ thể
CREATE TABLE content.Lessons (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    CourseId UUID REFERENCES content.Courses(Id) ON DELETE CASCADE,
    OrderIndex INT NOT NULL,
    LessonType VARCHAR(30) NOT NULL,            -- 'Standard', 'Vocabulary', 'Pinyin', 'Grammar'
    Status VARCHAR(20) DEFAULT 'Draft',
    XpReward INT DEFAULT 20,
    Version INT DEFAULT 1,
    CreatedAt TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE content.LessonTranslations (
    LessonId UUID REFERENCES content.Lessons(Id) ON DELETE CASCADE,
    BaseLanguage VARCHAR(10) NOT NULL,
    Title VARCHAR(255) NOT NULL,
    GuideText TEXT,                             -- Hướng dẫn lý thuyết trước bài tập
    PRIMARY KEY (LessonId, BaseLanguage)
);

-- Ngân hàng câu hỏi bài tập (Tách rời khỏi Lesson)
CREATE TABLE content.Questions (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    LessonId UUID REFERENCES content.Lessons(Id) ON DELETE CASCADE,
    OrderIndex INT NOT NULL,
    QuestionType VARCHAR(30) NOT NULL,          -- 'MultipleChoice', 'Translate', 'FillBlank', 'Pronounce'
    AudioPromptUrl VARCHAR(500),
    TargetText TEXT NOT NULL,                   -- Text tiếng Anh/Trung cần học
    PinyinOrIpa VARCHAR(255),                   -- Pinyin (nếu là tiếng Trung), IPA (nếu là tiếng Anh)
    GrammarNotes TEXT                           -- Giải thích ngữ pháp khi trả lời sai
);

CREATE TABLE content.QuestionTranslations (
    QuestionId UUID REFERENCES content.Questions(Id) ON DELETE CASCADE,
    BaseLanguage VARCHAR(10) NOT NULL,
    PromptTranslation TEXT NOT NULL,            -- Đề bài dịch sang tiếng mẹ đẻ
    Explanation TEXT,
    PRIMARY KEY (QuestionId, BaseLanguage)
);

CREATE TABLE content.QuestionOptions (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    QuestionId UUID REFERENCES content.Questions(Id) ON DELETE CASCADE,
    OptionText TEXT NOT NULL,
    PinyinOrIpa VARCHAR(255),
    IsCorrect BOOLEAN NOT NULL DEFAULT FALSE,
    OrderIndex INT NOT NULL DEFAULT 0
);
```

### 3.2. Phân hệ Tiến độ & Lịch sử làm bài (Learner Progress & Attempts)
```sql
CREATE SCHEMA learning;

-- Tiến độ tổng quan của khóa học
CREATE TABLE learning.UserCourseProgress (
    UserId UUID NOT NULL,
    CourseId UUID NOT NULL,
    CompletedLessonsCount INT DEFAULT 0,
    TotalLessonsCount INT NOT NULL,
    IsCompleted BOOLEAN DEFAULT FALSE,
    LastAccessedAt TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (UserId, CourseId)
);

-- Lịch sử từng lần học một bài
CREATE TABLE learning.ExerciseAttempts (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserId UUID NOT NULL,
    LessonId UUID NOT NULL,
    ScorePercentage INT NOT NULL,               -- 0 - 100%
    XpEarned INT DEFAULT 0,
    DurationSeconds INT NOT NULL,
    StartedAt TIMESTAMPTZ DEFAULT NOW(),
    CompletedAt TIMESTAMPTZ
);

-- Chi tiết từng câu trả lời của học viên
CREATE TABLE learning.QuestionResponses (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    AttemptId UUID REFERENCES learning.ExerciseAttempts(Id) ON DELETE CASCADE,
    QuestionId UUID NOT NULL,
    SelectedOptionId UUID,                      -- Nếu là trắc nghiệm
    UserInputText TEXT,                         -- Nếu là gõ chữ hoặc nhận diện giọng nói
    IsCorrect BOOLEAN NOT NULL,
    TimeSpentMs INT DEFAULT 0
);
```

### 3.3. Phân hệ Thuật toán Lặp lại Ngắt quãng (SRS SM-2 Standard)
```sql
CREATE SCHEMA srs;

-- Tiến độ thẻ từ vựng của học viên
CREATE TABLE srs.UserVocabularyProgress (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserId UUID NOT NULL,
    VocabularyId UUID NOT NULL,                 -- Liên kết tới từ vựng hoặc Question
    TargetLanguage VARCHAR(10) NOT NULL,        -- 'en', 'zh'
    Repetitions INT DEFAULT 0,
    EaseFactor FLOAT DEFAULT 2.5,               -- SM-2 Ease Factor chuẩn
    IntervalDays INT DEFAULT 0,                 -- Số ngày lặp lại kế tiếp
    NextReviewDate DATE NOT NULL,
    LastReviewDate TIMESTAMPTZ,
    State VARCHAR(20) DEFAULT 'Learning',       -- 'Learning', 'Review', 'Graduated'
    CONSTRAINT uq_user_vocab UNIQUE (UserId, VocabularyId)
);
CREATE INDEX idx_srs_user_due ON srs.UserVocabularyProgress (UserId, NextReviewDate);

-- Lịch sử đánh giá ôn tập (Audit Trail cho thuật toán)
CREATE TABLE srs.ReviewLogs (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserVocabularyId UUID REFERENCES srs.UserVocabularyProgress(Id) ON DELETE CASCADE,
    Rating INT NOT NULL,                        -- 1: Again, 2: Hard, 3: Good, 4: Easy
    OldEaseFactor FLOAT NOT NULL,
    NewEaseFactor FLOAT NOT NULL,
    OldIntervalDays INT NOT NULL,
    NewIntervalDays INT NOT NULL,
    ReviewedAt TIMESTAMPTZ DEFAULT NOW()
);
```

### 3.4. Phân hệ AI Tutor, Lịch sử Hội thoại & Hạn mức Chi phí (AI Usage Ledger)
```sql
CREATE SCHEMA ai;

CREATE TABLE ai.ChatSessions (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserId UUID NOT NULL,
    TargetLanguage VARCHAR(10) NOT NULL,
    ScenarioCode VARCHAR(50) NOT NULL,          -- 'JobInterview', 'BusinessEmail', 'FreeTalk'
    Title VARCHAR(255) NOT NULL,
    StartedAt TIMESTAMPTZ DEFAULT NOW(),
    EndedAt TIMESTAMPTZ,
    TotalMessagesCount INT DEFAULT 0
);

CREATE TABLE ai.ChatMessages (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    SessionId UUID REFERENCES ai.ChatSessions(Id) ON DELETE CASCADE,
    SenderType VARCHAR(10) NOT NULL,            -- 'User', 'Assistant'
    Content TEXT NOT NULL,
    PinyinOrIpa TEXT,                           -- AI sinh thêm phiên âm hỗ trợ
    Translation TEXT,                           -- Bản dịch tiếng mẹ đẻ (BaseLanguage)
    GrammarCorrection TEXT,                     -- Sửa lỗi câu nếu có
    CreatedAt TIMESTAMPTZ DEFAULT NOW()
);

-- Quản lý chi phí và Quota AI theo tài khoản (Ngăn chặn cháy ngân sách API)
CREATE TABLE ai.AIUsageLedger (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserId UUID NOT NULL,
    TaskType VARCHAR(30) NOT NULL,              -- 'TextExplanation', 'Roleplay', 'SpeechToText'
    ModelProvider VARCHAR(50) NOT NULL,         -- 'OpenAI_GPT4o_Mini', 'AzureSpeech'
    PromptTokens INT DEFAULT 0,
    CompletionTokens INT DEFAULT 0,
    TotalTokens INT DEFAULT 0,
    EstimatedCostUsd DECIMAL(10, 6) DEFAULT 0,  -- Chi phí USD thực tế của request
    CreatedAt TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_ai_usage_user_daily ON ai.AIUsageLedger (UserId, CreatedAt);
```

### 3.5. Phân hệ Thanh toán & Quyền lợi (Billing & Idempotent Webhook)
```sql
CREATE SCHEMA billing;

-- Gói dịch vụ
CREATE TABLE billing.SubscriptionPlans (
    Id VARCHAR(50) PRIMARY KEY,                 -- 'vip_monthly', 'vip_yearly'
    Name VARCHAR(100) NOT NULL,
    PriceVnd DECIMAL(18, 0) NOT NULL,
    DurationDays INT NOT NULL,
    AiDailyTokenQuota INT NOT NULL              -- Hạn mức token AI mỗi ngày
);

-- Trạng thái quyền lợi học viên (Single Source of Truth cho Authorization)
CREATE TABLE billing.UserEntitlements (
    UserId UUID PRIMARY KEY,
    Tier VARCHAR(20) DEFAULT 'Free',            -- 'Free', 'Vip'
    PlanId VARCHAR(50) REFERENCES billing.SubscriptionPlans(Id),
    ExpiresAt TIMESTAMPTZ,
    UpdatedAt TIMESTAMPTZ DEFAULT NOW()
);

-- Xử lý Webhook an toàn (Chống trùng lặp - Idempotent)
CREATE TABLE billing.WebhookInbox (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    Provider VARCHAR(50) NOT NULL,              -- 'VNPay', 'Stripe', 'Apple', 'Google'
    EventId VARCHAR(255) NOT NULL,              -- Mã giao dịch duy nhất từ Gateway
    Payload JSONB NOT NULL,
    ProcessedAt TIMESTAMPTZ,
    Status VARCHAR(20) DEFAULT 'Pending',       -- 'Pending', 'Processed', 'Failed'
    ErrorMessage TEXT,
    CONSTRAINT uq_provider_event UNIQUE (Provider, EventId)
);
```

---

## 4. CHIẾN LƯỢC TRIỂN KHAI AI TỪNG BƯỚC (PRAGMATIC AI ROADMAP)

Không cố gắng tự build model STT/chấm âm vị nội bộ khi chưa có tập dữ liệu kiểm thử. Thay vào đó, AI được triển khai theo 4 cấp độ kiểm soát rủi ro:

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        4 CẤP ĐỘ PHÁT TRIỂN AI                          │
├─────────────────┬─────────────────┬──────────────────┬─────────────────┤
│ V1: Text Tutor  │ V2: Voice Tutor │ V3: Pronounce    │ V4: Real-time   │
│ (MVP Tháng 1-2) │   (Tháng 3-4)   │   (Tháng 5-6)    │   (Tháng 7+)    │
├─────────────────┼─────────────────┼──────────────────┼─────────────────┤
│ • Giải thích từ │ • Ghi âm audio  │ • Chấm phát âm   │ • Voice stream  │
│ • Sửa ngữ pháp  │ • Whisper STT   │   âm vị (Azure   │   độ trễ thấp   │
│ • Roleplay Text │ • TTS phản hồi  │   Speech API)    │ • Tự ngắt lời   │
│ • Phân biệt     │ • Text-to-Audio │ • Chấm thanh điệu│   (VAD)         │
│   Anh / Trung   │   song ngữ      │   tiếng Trung    │                 │
└─────────────────┴─────────────────┴──────────────────┴─────────────────┘
```

### 4.1. V1 — Text Tutor & Roleplay (Phục vụ MVP)
- Sử dụng mô hình chi phí thấp, tốc độ cao (`gpt-4o-mini` hoặc `claude-3-haiku`).
- **Structured Output**: Ràng buộc model trả về JSON nghiêm ngặt:
  ```json
  {
    "reply_text": "你好！很高兴认识你。",
    "pinyin_or_ipa": "Nǐ hǎo! Hěn gāoxìng rènshí nǐ.",
    "translation": "Chào bạn! Rất vui được làm quen với bạn.",
    "grammar_tips": "Cụm từ '很高兴' thường dùng trong lần đầu gặp mặt."
  }
  ```
- **Context Whitelist**: Inject ngữ cảnh trình độ người học (ví dụ: "Người học đang ở trình độ HSK 1, chỉ trả lời bằng các từ vựng trong danh sách HSK 1").

### 4.2. V3 — Đánh giá Phát âm Thực tế (Không hứa hẹn 96% phi thực tế)
- Tận dụng **Azure Speech Service (Pronunciation Assessment)** thay vì tự huấn luyện mô hình.
- Phân tích rõ ràng giới hạn kỹ thuật: Thanh điệu tiếng Trung có hiện tượng **biến điệu (Tone Sandhi)** (ví dụ: hai thanh 3 đi liền nhau thì thanh đầu đọc thành thanh 2: `Nǐ hǎo` -> `Ní hǎo`). Hệ thống phải dựa vào phiên âm câu thực tế để chấm, không chấm thanh điệu tách rời từng từ.

---

## 5. BẢO MẬT, QUYỀN RIÊNG TƯ & VẬN HÀNH

1. **Ủy quyền dựa trên tài nguyên (Resource-Based Authorization)**:
   - Không chỉ kiểm tra xem user có token hợp lệ hay không (`[Authorize]`).
   - Mọi request vào bài học nâng cao đều phải kiểm tra quyền trong `billing.UserEntitlements`:
     ```csharp
     if (lesson.IsPremium && userEntitlement.Tier != "Vip")
     {
         return Forbid("Bài học này yêu cầu gói tài khoản VIP.");
     }
     ```
2. **Vòng đời dữ liệu giọng nói (Audio Privacy Lifecycle)**:
   - File ghi âm giọng nói của người học chỉ lưu tạm trên Object Storage trong **7 ngày** để phục vụ việc nghe lại bài tập của học viên.
   - Sau 7 ngày, một Background Job (IHostedService) sẽ tự động dọn dẹp file để đảm bảo quyền riêng tư và tiết kiệm chi phí lưu trữ.
3. **Bảo vệ chống lặp Webhook (Webhook Replay Protection)**:
   - Mọi webhook từ cổng thanh toán đều phải đi qua bảng `billing.WebhookInbox`.
   - Nếu `EventId` đã tồn tại -> Return ngay `200 OK` mà không kích hoạt quyền lợi hai lần.
4. **Bản quyền Video HLS**:
   - Sử dụng mã hóa HLS AES-128 kết hợp URL ký số (Signed URLs) có thời hạn 10 phút.
   - Minh bạch về mặt kỹ thuật: Đây là giải pháp **hạn chế tải lậu ở mức phổ thông**, không coi là DRM phần cứng chống sao chép tuyệt đối (Widevine L1).

---

## 6. CẤU TRÚC REPOSITORY & SOLUTION DỰ ÁN

Tổ chức source code theo chuẩn Clean Architecture bên trong repo mới `linguaflow` (hoặc `linguabridge`):

```text
AppInHouse/
├── repos/
│   ├── identity/                                    # Cụm Identity STS (Đang chạy cổng 44310, 44303)
│   └── linguabridge/                                # [REPO MỚI] Ứng dụng học ngoại ngữ
│       ├── LinguaBridge.sln                         # Solution Backend .NET 10 LTS
│       │
│       ├── src/
│       │   ├── Core/
│       │   │   ├── LinguaBridge.Domain/             # Thực thể, Enums, ValueObjects chung
│       │   │   └── LinguaBridge.Application/        # CQRS MediatR, Interfaces, DTOs
│       │   │
│       │   ├── Modules/                             # Các module ranh giới độc lập
│       │   │   ├── LinguaBridge.Modules.Learning/   # Curriculum, Quiz, Progress, SRS SM-2
│       │   │   ├── LinguaBridge.Modules.AiTutor/    # Prompts, LLM Provider, Usage Ledger
│       │   │   ├── LinguaBridge.Modules.Content/    # Course Builder, Translations, Publishing
│       │   │   └── LinguaBridge.Modules.Billing/    # Subscriptions, Webhook Inbox
│       │   │
│       │   ├── Infrastructure/
│       │   │   ├── LinguaBridge.Infrastructure.Persistence/ # EF Core, Postgres Schemas
│       │   │   └── LinguaBridge.Infrastructure.External/    # OpenAI, Azure Speech, Object Storage
│       │   │
│       │   └── Presentation/
│       │       └── LinguaBridge.Api/                # Single ASP.NET Core Host (Chạy trên cổng 44320)
│       │
│       ├── mobile/                                  # Flutter App (iOS / Android)
│       │   ├── lib/
│       │   │   ├── features/ (learn, srs, ai_tutor, courses, profile)
│       │   │   └── core/ (network, audio, i18n, storage)
│       │
│       └── web/                                     # Next.js 14 App Router
│           ├── src/app/ (learn, studio, admin)
│           └── src/components/
```

---

## 7. ROADMAP 4 MỐC NGHIỆM THU CHO SOLO DEVELOPER

Lộ trình được thiết kế lại thực tế, mỗi mốc đều có sản phẩm chạy được (Deliverable) để nghiệm thu:

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        ROADMAP 4 MỐC THỰC CHIẾN                        │
├────────────────────┬────────────────────┬──────────────────────────────┤
│ MỐC 1: NỀN TẢNG    │ MỐC 2: AI TUTOR    │ MỐC 3: BETA & DOANH THU      │
│ (Tuần 1 - 6)       │ (Tuần 7 - 12)      │ (Tuần 13 - 18)               │
├────────────────────┼────────────────────┼──────────────────────────────┤
│ • Dựng Modular     │ • AI Text Tutor    │ • Ghi âm giọng nói           │
│   Monolith .NET    │ • Sửa câu & ngữ    │   và phát âm cơ bản          │
│ • Tích hợp STS     │   pháp Anh - Trung │ • Placement Test             │
│ • Quản trị CMS bài │ • Roleplay Text    │ • Cổng thanh toán (VNPay /   │
│   học cơ bản       │ • Kiểm soát Token  │   In-App Purchase)           │
│ • Flutter + Web    │   và Quota User    │ • Thử nghiệm 50 học viên     │
│   học lộ trình     │                    │                              │
│ • SRS SM-2 chuẩn   │                    │                              │
└────────────────────┴────────────────────┴──────────────────────────────┘
                                  │
                                  ▼
┌────────────────────────────────────────────────────────────────────────┐
│ MỐC 4: SCALE & MỞ RỘNG (Tuần 19 - 26)                                  │
│ • Voice Roleplay đàm thoại hai chiều                                   │
│ • Video LMS và khóa học chuyên sâu                                     │
│ • Luyện viết chữ Hán Canvas                                            │
│ • Tối ưu Performance & Trích xuất Background Worker nếu cần            │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 8. TIÊU CHÍ NGHIỆM THU (DEFINITION OF DONE) TRƯỚC KHI BẮT ĐẦU

- [ ] **Auth đồng bộ**: Đăng nhập một tài khoản duy nhất trên cả Web và Mobile thông qua `AppInHouse.Identity.STS`.
- [ ] **Đồng bộ tiến độ**: Học 1 câu trên Web, mở Mobile thấy ngay tiến độ đã cập nhật.
- [ ] **SRS hoạt động đúng**: Chỉ các thẻ có `NextReviewDate <= CURRENT_DATE` mới xuất hiện trong phiên ôn tập; sau khi chọn đánh giá, thẻ được cập nhật ngày lặp lại chính xác theo thuật toán SM-2.
- [ ] **Content Lifecycle**: Admin có thể tạo bài học ở trạng thái `Draft`, chỉnh sửa, xem trước (Preview) và chỉ khi bấm `Published` học viên mới nhìn thấy.
- [ ] **AI Budget Protection**: User hết hạn mức token trong ngày sẽ nhận thông báo nâng cấp hoặc đợi qua ngày mới, không thể spam làm phát sinh chi phí ngoài tầm kiểm soát.
- [ ] **Webhook An toàn**: Gửi thử cùng một webhook thanh toán 5 lần liên tiếp, hệ thống chỉ kích hoạt tài khoản đúng 1 lần duy nhất (`WebhookInbox` deduplication).
