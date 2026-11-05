-- Sample courses for LinguaBridge: content (courses, lessons, questions, translations) and, optionally, due SRS cards.
-- See docs/handoffs/TASK-025. Run it with scripts/seed-sample-courses.ps1, or by hand:
--   psql -U postgres -d LinguaBridge -v ON_ERROR_STOP=1 -v user_id=<learner guid> -f seed-sample-courses.sql
--
-- * Safe to run again: every row has a fixed id derived from its name (md5 -> uuid) and is inserted with
--   ON CONFLICT DO NOTHING, so nothing is duplicated and nothing an administrator has edited since is overwritten.
-- * Content is created Published, so learners see it at once.
-- * user_id (optional) is the learner who gets one SRS card per vocabulary question, already due, so /flashcards.html has
--   cards to flip. Cards that exist (and so any review progress) are never reset. Without it only content is created.
-- * Values follow the application's conventions: languages 'en' / 'zh' / 'vi', levels 'A1' / 'A2' / 'HSK1', statuses 'Published'.
\set ON_ERROR_STOP on
\encoding UTF8

BEGIN;

-- ---- Source data ---------------------------------------------------------------------------------------------------

CREATE TEMP TABLE s_courses AS
SELECT * FROM (VALUES
  ('ENG-A1-COMM', 'en', 'A1',
   'Tiếng Anh giao tiếp nhập môn', 'Beginner English Communication',
   'Những mẫu câu đầu tiên để chào hỏi, giới thiệu bản thân và trao đổi ở nơi làm việc.',
   'Your first phrases for greeting people, introducing yourself and talking at work.'),
  ('ENG-BIZ-A2', 'en', 'A2',
   'Tiếng Anh công sở & thuyết trình', 'Workplace English & Presentations',
   'Viết email chuyên nghiệp và trình bày ý tưởng rõ ràng trong môi trường công sở.',
   'Write professional emails and present your ideas clearly at work.'),
  ('ZH-HSK1-COMM', 'zh', 'HSK1',
   'Tiếng Trung giao tiếp HSK 1', 'Conversational Chinese HSK 1',
   'Từ vựng và mẫu câu HSK 1 có chữ Hán, pinyin và nghĩa tiếng Việt.',
   'HSK 1 words and sentences with characters, pinyin and English meanings.')
) AS v(code, target, level, title_vi, title_en, desc_vi, desc_en);

CREATE TEMP TABLE s_lessons AS
SELECT * FROM (VALUES
  ('ENG-A1-COMM', 1, 'Vocabulary', 15,
   'Chào hỏi & giới thiệu bản thân (Greetings & Introductions)', 'Greetings & Introductions',
   'Học cách chào hỏi và nói tên của bạn.', 'Learn how to greet people and say your name.'),
  ('ENG-A1-COMM', 2, 'Standard', 20,
   'Giao tiếp công sở cơ bản (Basic Workplace Communication)', 'Basic Workplace Communication',
   'Những câu đơn giản để nhờ giúp đỡ và trao đổi công việc.', 'Simple sentences for asking for help and talking about work.'),
  ('ENG-BIZ-A2', 1, 'Standard', 25,
   'Viết email công việc (Professional Email Writing)', 'Professional Email Writing',
   'Cấu trúc một email công việc: mở đầu, nội dung, kết thúc.', 'The parts of a business email: opening, body and closing.'),
  ('ZH-HSK1-COMM', 1, 'Vocabulary', 15,
   'Chào hỏi & cảm ơn (问候与感谢 - Wènhòu yǔ Gǎnxiè)', 'Greetings & Thanks (问候与感谢 - Wènhòu yǔ Gǎnxiè)',
   'Những từ đầu tiên để chào và cảm ơn.', 'The first words for greeting and thanking people.'),
  ('ZH-HSK1-COMM', 2, 'Vocabulary', 20,
   'Giới thiệu nghề nghiệp (职业介绍 - Zhíyè Jièshào)', 'Introducing Your Job (职业介绍 - Zhíyè Jièshào)',
   'Nói về nghề nghiệp của bạn và hỏi nghề của người khác.', 'Talk about your job and ask about other people''s.')
) AS v(course, idx, lesson_type, xp, title_vi, title_en, guide_vi, guide_en);

-- kind T = Translate: typed answer, and the vocabulary that becomes SRS cards. target_text is the word or phrase in the
--   language being learnt; prompt_* is its meaning (what the back of the card shows); expl_* is a usage note.
-- kind M = MultipleChoice: target_text is the question (or the word asked about); prompt_* is its translation or a hint.
CREATE TEMP TABLE s_questions AS
SELECT * FROM (VALUES
  -- ENG-A1-COMM / 1: greetings
  ('ENG-A1-COMM', 1, 1, 'T', 'Hello', '/həˈləʊ/', 'Xin chào', 'A greeting for any time of day', 'Lời chào dùng được vào mọi lúc trong ngày.', 'Works at any time of day, formal or informal.'),
  ('ENG-A1-COMM', 1, 2, 'T', 'Good morning', '/ɡʊd ˈmɔːnɪŋ/', 'Chào buổi sáng', 'A greeting used before noon', 'Dùng trước buổi trưa.', 'Used until about noon.'),
  ('ENG-A1-COMM', 1, 3, 'T', 'Nice to meet you', '/naɪs tə miːt juː/', 'Rất vui được gặp bạn', 'Said when you meet someone for the first time', 'Nói khi gặp ai đó lần đầu.', 'Said the first time you meet someone.'),
  ('ENG-A1-COMM', 1, 4, 'T', 'My name is Lan', '/maɪ neɪm ɪz lɑːn/', 'Tên tôi là Lan', 'How to say your name', 'Dùng "My name is" rồi đến tên của bạn.', 'Use "My name is" followed by your name.'),
  ('ENG-A1-COMM', 1, 5, 'T', 'How are you?', '/haʊ ɑː juː/', 'Bạn khỏe không?', 'A polite question about how someone feels', 'Câu hỏi lịch sự về sức khỏe, tâm trạng.', 'A polite question; answer, then ask back.'),
  ('ENG-A1-COMM', 1, 6, 'M', 'You meet a new colleague. What do you say?', NULL, 'Bạn gặp một đồng nghiệp mới. Bạn nói gì?', 'Choose the polite way to meet someone', 'Chọn cách chào lịch sự khi mới gặp.', 'Greet and show you are glad to meet them.'),
  ('ENG-A1-COMM', 1, 7, 'M', 'Someone asks "How are you?". What is a good answer?', NULL, 'Ai đó hỏi "How are you?". Trả lời thế nào cho phù hợp?', 'Answer, then ask back', 'Trả lời rồi hỏi lại cho lịch sự.', 'A full answer: feeling, thanks, then "And you?".'),
  -- ENG-A1-COMM / 2: workplace
  ('ENG-A1-COMM', 2, 1, 'T', 'Could you help me?', '/kʊd juː help miː/', 'Bạn giúp tôi được không?', 'A polite request for help', '"Could" làm lời nhờ vả lịch sự hơn.', '"Could" makes a request polite.'),
  ('ENG-A1-COMM', 2, 2, 'T', 'Meeting room', '/ˈmiːtɪŋ ruːm/', 'Phòng họp', 'The room where a meeting takes place', 'Nơi diễn ra cuộc họp.', 'Where meetings are held.'),
  ('ENG-A1-COMM', 2, 3, 'T', 'Deadline', '/ˈdedlaɪn/', 'Hạn chót', 'The latest time to finish a task', 'Thời điểm muộn nhất phải xong việc.', 'The last moment to finish a task.'),
  ('ENG-A1-COMM', 2, 4, 'T', 'Please send me the file', '/pliːz send miː ðə faɪl/', 'Vui lòng gửi cho tôi tập tin', 'A polite request for a document', 'Thêm "please" khi nhờ ai làm việc gì.', 'Add "please" when you ask for something.'),
  ('ENG-A1-COMM', 2, 5, 'T', 'Thank you for your help', '/θæŋk juː fɔː jɔː help/', 'Cảm ơn sự giúp đỡ của bạn', 'A polite way to thank a colleague', 'Cách cảm ơn đồng nghiệp lịch sự.', 'A polite way to thank a colleague.'),
  ('ENG-A1-COMM', 2, 6, 'M', 'You need a colleague''s help. Which sentence is the most polite?', NULL, 'Bạn cần đồng nghiệp giúp. Câu nào lịch sự nhất?', 'Polite requests use "Could you ...?"', 'Lời nhờ lịch sự dùng "Could you ...?".', 'Polite requests use "Could you ...?".'),
  ('ENG-A1-COMM', 2, 7, 'M', 'What is a "deadline"?', NULL, '"Deadline" là gì?', 'It is about time', 'Đây là từ chỉ thời gian.', 'It is the final date or time for a task.'),
  -- ENG-BIZ-A2 / 1: email
  ('ENG-BIZ-A2', 1, 1, 'T', 'Dear Mr. Smith,', '/dɪə ˈmɪstə smɪθ/', 'Kính gửi ông Smith,', 'A formal way to begin an email', 'Cách mở đầu email trang trọng.', 'A formal opening, followed by a comma.'),
  ('ENG-BIZ-A2', 1, 2, 'T', 'I am writing to inform you', '/aɪ æm ˈraɪtɪŋ tə ɪnˈfɔːm juː/', 'Tôi viết thư này để thông báo', 'States why you are writing', 'Dùng để nêu mục đích của email.', 'Use it to state the purpose of your email.'),
  ('ENG-BIZ-A2', 1, 3, 'T', 'Please find attached', '/pliːz faɪnd əˈtætʃt/', 'Vui lòng xem tệp đính kèm', 'Introduces a file attached to the email', 'Giới thiệu tệp bạn đính kèm.', 'Introduces the file you attach.'),
  ('ENG-BIZ-A2', 1, 4, 'T', 'Looking forward to hearing from you', '/ˈlʊkɪŋ ˈfɔːwəd tə ˈhɪərɪŋ frɒm juː/', 'Mong sớm nhận được phản hồi của bạn', 'A friendly closing that invites a reply', 'Câu kết thân thiện, mong người nhận trả lời.', 'A friendly closing line that invites a reply.'),
  ('ENG-BIZ-A2', 1, 5, 'T', 'Best regards', '/best rɪˈɡɑːdz/', 'Trân trọng', 'A standard closing before your name', 'Lời kết thông dụng trước tên của bạn.', 'A standard closing before your name.'),
  ('ENG-BIZ-A2', 1, 6, 'M', 'Which opening suits a formal email to a manager?', NULL, 'Cách mở đầu nào hợp với email trang trọng gửi quản lý?', 'Formal emails start with "Dear ..."', 'Email trang trọng mở đầu bằng "Dear ...".', 'Formal emails start with "Dear ...".'),
  ('ENG-BIZ-A2', 1, 7, 'M', 'Which closing is appropriate at the end of a business email?', NULL, 'Lời kết nào phù hợp ở cuối email công việc?', 'Polite and neutral', 'Lịch sự và trung tính.', '"Best regards" is polite and neutral.'),
  -- ZH-HSK1-COMM / 1: greetings and thanks
  ('ZH-HSK1-COMM', 1, 1, 'T', '你好', 'nǐ hǎo', 'Xin chào', 'Hello', 'Lời chào phổ biến nhất.', 'The most common greeting.'),
  ('ZH-HSK1-COMM', 1, 2, 'T', '谢谢', 'xièxie', 'Cảm ơn', 'Thank you', 'Dùng để cảm ơn.', 'Used to say thank you.'),
  ('ZH-HSK1-COMM', 1, 3, 'T', '不客气', 'bú kèqi', 'Không có chi', 'You are welcome', 'Đáp lại khi ai đó nói 谢谢.', 'The reply to 谢谢.'),
  ('ZH-HSK1-COMM', 1, 4, 'T', '再见', 'zàijiàn', 'Tạm biệt', 'Goodbye', 'Lời chào khi chia tay.', 'Said when you part.'),
  ('ZH-HSK1-COMM', 1, 5, 'T', '对不起', 'duìbuqǐ', 'Xin lỗi', 'Sorry', 'Dùng khi bạn có lỗi.', 'Used when you made a mistake.'),
  ('ZH-HSK1-COMM', 1, 6, 'M', '谢谢', 'xièxie', 'Chọn nghĩa đúng của từ này.', 'Choose the correct meaning of this word.', '谢谢 nghĩa là "cảm ơn".', '谢谢 means "thank you".'),
  ('ZH-HSK1-COMM', 1, 7, 'M', '你好', 'nǐ hǎo', 'Bạn nói gì khi gặp ai đó?', 'What do you say when you meet someone?', '你好 là lời chào hằng ngày.', '你好 is the everyday greeting.'),
  -- ZH-HSK1-COMM / 2: jobs
  ('ZH-HSK1-COMM', 2, 1, 'T', '老师', 'lǎoshī', 'Giáo viên', 'Teacher', 'Người dạy học.', 'A person who teaches.'),
  ('ZH-HSK1-COMM', 2, 2, 'T', '学生', 'xuésheng', 'Học sinh', 'Student', 'Người đi học.', 'A person who studies.'),
  ('ZH-HSK1-COMM', 2, 3, 'T', '医生', 'yīshēng', 'Bác sĩ', 'Doctor', 'Người khám và chữa bệnh.', 'A person who treats patients.'),
  ('ZH-HSK1-COMM', 2, 4, 'T', '我是老师', 'wǒ shì lǎoshī', 'Tôi là giáo viên', 'I am a teacher', 'Mẫu câu: 我是 + nghề nghiệp.', 'Pattern: 我是 + job.'),
  ('ZH-HSK1-COMM', 2, 5, 'T', '你做什么工作？', 'nǐ zuò shénme gōngzuò?', 'Bạn làm nghề gì?', 'What do you do for a living?', 'Câu hỏi về nghề nghiệp.', 'Asks about someone''s job.'),
  ('ZH-HSK1-COMM', 2, 6, 'M', '医生', 'yīshēng', 'Chọn nghĩa đúng của từ này.', 'Choose the correct meaning of this word.', '医生 nghĩa là "bác sĩ".', '医生 means "doctor".'),
  ('ZH-HSK1-COMM', 2, 7, 'M', '我是学生', 'wǒ shì xuésheng', 'Câu này nghĩa là gì?', 'What does this sentence mean?', '我是学生 nghĩa là "tôi là học sinh".', '我是学生 means "I am a student".')
) AS v(course, lesson, idx, kind, target_text, ipa, prompt_vi, prompt_en, expl_vi, expl_en);

-- Answer choices of the M questions: exactly one is correct. Its position varies (1, 2 or 3) so the right answer is not
-- always the first one; the web client also shuffles the choices when it shows them.
CREATE TEMP TABLE s_options AS
SELECT * FROM (VALUES
  ('ENG-A1-COMM', 1, 6, 1, 'See you tomorrow.', false),
  ('ENG-A1-COMM', 1, 6, 2, 'Nice to meet you.', true),
  ('ENG-A1-COMM', 1, 6, 3, 'I am hungry.', false),
  ('ENG-A1-COMM', 1, 7, 1, 'My name is Lan.', false),
  ('ENG-A1-COMM', 1, 7, 2, 'Good night.', false),
  ('ENG-A1-COMM', 1, 7, 3, 'I am fine, thank you. And you?', true),
  ('ENG-A1-COMM', 2, 6, 1, 'Could you help me, please?', true),
  ('ENG-A1-COMM', 2, 6, 2, 'Help me now.', false),
  ('ENG-A1-COMM', 2, 6, 3, 'You must help me.', false),
  ('ENG-A1-COMM', 2, 7, 1, 'A room for meetings', false),
  ('ENG-A1-COMM', 2, 7, 2, 'A kind of email', false),
  ('ENG-A1-COMM', 2, 7, 3, 'The last time to finish a task', true),
  ('ENG-BIZ-A2', 1, 6, 1, 'Hey you,', false),
  ('ENG-BIZ-A2', 1, 6, 2, 'Dear Ms. Tran,', true),
  ('ENG-BIZ-A2', 1, 6, 3, 'What''s up?', false),
  ('ENG-BIZ-A2', 1, 7, 1, 'Best regards, Minh', true),
  ('ENG-BIZ-A2', 1, 7, 2, 'See ya!', false),
  ('ENG-BIZ-A2', 1, 7, 3, 'Whatever.', false),
  ('ZH-HSK1-COMM', 1, 6, 1, 'Goodbye / Tạm biệt', false),
  ('ZH-HSK1-COMM', 1, 6, 2, 'Sorry / Xin lỗi', false),
  ('ZH-HSK1-COMM', 1, 6, 3, 'Thank you / Cảm ơn', true),
  ('ZH-HSK1-COMM', 1, 7, 1, '再见', false),
  ('ZH-HSK1-COMM', 1, 7, 2, '你好', true),
  ('ZH-HSK1-COMM', 1, 7, 3, '对不起', false),
  ('ZH-HSK1-COMM', 2, 6, 1, 'Doctor / Bác sĩ', true),
  ('ZH-HSK1-COMM', 2, 6, 2, 'Teacher / Giáo viên', false),
  ('ZH-HSK1-COMM', 2, 6, 3, 'Student / Học sinh', false),
  ('ZH-HSK1-COMM', 2, 7, 1, 'I am a teacher / Tôi là giáo viên', false),
  ('ZH-HSK1-COMM', 2, 7, 2, 'I am a student / Tôi là học sinh', true),
  ('ZH-HSK1-COMM', 2, 7, 3, 'I am a doctor / Tôi là bác sĩ', false)
) AS v(course, lesson, question, idx, option_text, is_correct);

-- Fixed ids: the same name always gives the same uuid, which is what makes re-running harmless.
CREATE FUNCTION pg_temp.sid(VARIADIC parts text[]) RETURNS uuid
  LANGUAGE sql IMMUTABLE AS $$ SELECT md5('linguabridge-sample:' || array_to_string(parts, ':'))::uuid $$;

-- ---- Content -------------------------------------------------------------------------------------------------------

INSERT INTO content.courses (id, code, target_language, level, status, version, thumbnail_url, created_at, updated_at)
SELECT pg_temp.sid('course', code), code, target, level, 'Published', 1, NULL, now(), now() FROM s_courses
ON CONFLICT DO NOTHING;

INSERT INTO content.course_translations (course_id, base_language, title, description)
SELECT pg_temp.sid('course', code), 'vi', title_vi, desc_vi FROM s_courses
UNION ALL
SELECT pg_temp.sid('course', code), 'en', title_en, desc_en FROM s_courses
ON CONFLICT DO NOTHING;

INSERT INTO content.lessons (id, course_id, order_index, lesson_type, status, xp_reward, version, created_at)
SELECT pg_temp.sid('lesson', course, idx::text), pg_temp.sid('course', course), idx, lesson_type, 'Published', xp, 1, now() FROM s_lessons
ON CONFLICT DO NOTHING;

INSERT INTO content.lesson_translations (lesson_id, base_language, title, guide_text)
SELECT pg_temp.sid('lesson', course, idx::text), 'vi', title_vi, guide_vi FROM s_lessons
UNION ALL
SELECT pg_temp.sid('lesson', course, idx::text), 'en', title_en, guide_en FROM s_lessons
ON CONFLICT DO NOTHING;

INSERT INTO content.questions (id, lesson_id, order_index, question_type, audio_prompt_url, target_text, pinyin_or_ipa, grammar_notes)
SELECT pg_temp.sid('question', course, lesson::text, idx::text), pg_temp.sid('lesson', course, lesson::text), idx,
       CASE kind WHEN 'T' THEN 'Translate' ELSE 'MultipleChoice' END, NULL, target_text, ipa, NULL
FROM s_questions
ON CONFLICT DO NOTHING;

INSERT INTO content.question_translations (question_id, base_language, prompt_translation, explanation)
SELECT pg_temp.sid('question', course, lesson::text, idx::text), 'vi', prompt_vi, expl_vi FROM s_questions
UNION ALL
SELECT pg_temp.sid('question', course, lesson::text, idx::text), 'en', prompt_en, expl_en FROM s_questions
ON CONFLICT DO NOTHING;

INSERT INTO content.question_options (id, question_id, option_text, pinyin_or_ipa, is_correct, order_index)
SELECT pg_temp.sid('option', course, lesson::text, question::text, idx::text),
       pg_temp.sid('question', course, lesson::text, question::text), option_text, NULL, is_correct, idx
FROM s_options
ON CONFLICT DO NOTHING;

-- ---- SRS cards (only when a learner is given) ----------------------------------------------------------------------

\if :{?user_id}
-- One card per vocabulary (Translate) question, due since yesterday. A card that exists keeps its review progress.
INSERT INTO srs.user_vocabulary_progress
  (id, user_id, vocabulary_id, target_language, repetitions, ease_factor, interval_days, next_review_date, last_review_date, state)
SELECT pg_temp.sid('card', :'user_id', q.course, q.lesson::text, q.idx::text),
       :'user_id'::uuid, pg_temp.sid('question', q.course, q.lesson::text, q.idx::text),
       c.target, 0, 2.5, 0, current_date - 1, NULL, 'Learning'
FROM s_questions q JOIN s_courses c ON c.code = q.course
WHERE q.kind = 'T'
ON CONFLICT DO NOTHING;
\endif

COMMIT;

-- ---- What is there now ---------------------------------------------------------------------------------------------
SELECT c.code, c.target_language AS lang, c.level, c.status,
       (SELECT count(*) FROM content.lessons l WHERE l.course_id = c.id) AS lessons,
       (SELECT count(*) FROM content.questions q JOIN content.lessons l ON l.id = q.lesson_id WHERE l.course_id = c.id) AS questions
FROM content.courses c
WHERE c.id IN (SELECT pg_temp.sid('course', code) FROM s_courses)
ORDER BY c.code;
