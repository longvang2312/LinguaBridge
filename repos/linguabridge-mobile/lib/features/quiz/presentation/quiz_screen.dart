import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/router/app_routes.dart';
import '../application/quiz_controller.dart';
import 'question_view.dart';
import 'result_view.dart';

const _praise = ['Chính xác!', 'Làm tốt lắm!', 'Tuyệt vời!'];

/// Runs one lesson full screen (it lives outside the tab shell, so there is no bottom bar): questions one at a time,
/// instant feedback, then the result.
class QuizScreen extends ConsumerStatefulWidget {
  const QuizScreen({super.key, required this.lessonId, this.courseId});

  final String lessonId;

  /// The course the lesson belongs to, when the caller knows it (only used to find the way back).
  final String? courseId;

  @override
  ConsumerState<QuizScreen> createState() => _QuizScreenState();
}

class _QuizScreenState extends ConsumerState<QuizScreen> {
  final _text = TextEditingController();

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  QuizController get _controller => ref.read(quizControllerProvider(widget.lessonId).notifier);

  void _leave() {
    if (context.canPop()) {
      context.pop();
    } else {
      context.go(AppRoutes.courses);
    }
  }

  Future<void> _confirmExit() async {
    final leave = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        icon: const Icon(Icons.exit_to_app),
        title: const Text('Thoát bài học?'),
        content: const Text('Lượt làm bài này sẽ bị hủy và bạn sẽ không nhận được XP.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Ở lại học tiếp')),
          TextButton(onPressed: () => Navigator.pop(context, true), child: const Text('Thoát')),
        ],
      ),
    );
    if (leave == true && mounted) _leave();
  }

  /// The X: nothing to lose before the quiz is open or after it is done; in between the learner is asked.
  void _onExit(QuizState state) {
    if (state.phase == QuizPhase.answering || state.phase == QuizPhase.feedback) {
      _confirmExit();
    } else {
      _leave();
    }
  }

  @override
  Widget build(BuildContext context) {
    final provider = quizControllerProvider(widget.lessonId);
    final state = ref.watch(provider);
    // A new question starts with an empty answer box.
    ref.listen(provider.select((s) => (s.phase == QuizPhase.loading || s.phase == QuizPhase.completed) ? -1 : s.index), (_, _) => _text.clear());

    final inProgress = state.phase == QuizPhase.answering || state.phase == QuizPhase.feedback;
    return PopScope(
      canPop: !inProgress,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _confirmExit();
      },
      child: Scaffold(
        appBar: _TopBar(state: state, onExit: () => _onExit(state)),
        body: switch (state.phase) {
          QuizPhase.loading => const Center(child: Column(mainAxisSize: MainAxisSize.min, children: [CircularProgressIndicator(), SizedBox(height: 12), Text('Đang chuẩn bị bài học...')])),
          QuizPhase.error => _ErrorBody(state: state, onRetry: _controller.restart, onBack: _leave),
          QuizPhase.completed => ResultView(outcome: state.outcome!, busy: state.busy, onBack: _leave, onAgain: _controller.restart),
          _ => _questionBody(state),
        },
      ),
    );
  }

  Widget _questionBody(QuizState state) {
    final question = state.question;
    if (question == null) return const SizedBox.shrink();
    final checked = state.phase == QuizPhase.feedback;

    return Column(children: [
      Expanded(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(16),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 640),
              // Each question is a new widget that fades in by itself. It must not be cross-faded with the previous one
              // (AnimatedSwitcher): while both are alive the old answer box still holds the keyboard focus, so the new
              // box's autofocus is ignored and the keyboard closes between two typed questions.
              child: TweenAnimationBuilder<double>(
                key: ValueKey('${state.attemptId}-${question.id}'),
                tween: Tween(begin: 0, end: 1),
                duration: const Duration(milliseconds: 250),
                curve: Curves.easeOut,
                builder: (context, t, child) => Opacity(opacity: t, child: Transform.translate(offset: Offset(0, 12 * (1 - t)), child: child)),
                child: QuestionView(
                  question: question,
                  options: state.options,
                  selectedOptionId: state.selectedOptionId,
                  checked: checked,
                  result: state.result,
                  textController: _text,
                  onSelect: _controller.selectOption,
                  onSubmitted: () => _controller.submit(typedText: _text.text),
                ),
              ),
            ),
          ),
        ),
      ),
      ListenableBuilder(listenable: _text, builder: (context, _) => _AnswerBar(state: state, typedText: _text.text, controller: _controller)),
    ]);
  }
}

class _TopBar extends StatelessWidget implements PreferredSizeWidget {
  const _TopBar({required this.state, required this.onExit});

  final QuizState state;
  final VoidCallback onExit;

  @override
  Size get preferredSize => const Size.fromHeight(kToolbarHeight);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final position = state.quiz == null || state.total == 0 ? '' : 'Câu ${(state.index + 1).clamp(1, state.total)}/${state.total}';
    return AppBar(
      backgroundColor: scheme.surface,
      foregroundColor: scheme.onSurface,
      surfaceTintColor: Colors.transparent,
      leading: IconButton(key: const Key('quiz-exit'), tooltip: 'Thoát bài học', icon: const Icon(Icons.close), onPressed: onExit),
      title: TweenAnimationBuilder<double>(
        tween: Tween(begin: 0, end: state.progress),
        duration: const Duration(milliseconds: 400),
        curve: Curves.easeOutCubic,
        builder: (context, value, _) => LinearProgressIndicator(key: const Key('quiz-progress'), value: value, minHeight: 12, borderRadius: BorderRadius.circular(8), color: Colors.green.shade500, backgroundColor: scheme.surfaceContainerHighest),
      ),
      actions: [
        Padding(
          padding: const EdgeInsets.only(right: 16, left: 8),
          child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
            Text(position, key: const Key('quiz-position'), style: theme.textTheme.labelLarge?.copyWith(fontWeight: FontWeight.w700)),
            if (state.quiz != null)
              Row(mainAxisSize: MainAxisSize.min, children: [
                Icon(Icons.bolt, size: 14, color: Colors.amber.shade800),
                Text('Tối đa ${state.quiz!.xpReward} XP', key: const Key('quiz-xp'), style: theme.textTheme.labelSmall),
              ]),
          ]),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.state, required this.onRetry, required this.onBack});

  final QuizState state;
  final VoidCallback onRetry;
  final VoidCallback onBack;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final message = state.notFound ? 'Không tìm thấy bài học này hoặc bài học chưa được xuất bản.' : (state.error ?? 'Không mở được bài học.');
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(state.notFound ? Icons.search_off : Icons.cloud_off, size: 48, color: theme.colorScheme.error),
          const SizedBox(height: 12),
          Text(message, textAlign: TextAlign.center, style: theme.textTheme.bodyLarge),
          const SizedBox(height: 16),
          if (!state.notFound) FilledButton.icon(onPressed: onRetry, icon: const Icon(Icons.refresh), label: const Text('Thử lại'), style: FilledButton.styleFrom(minimumSize: const Size(180, 44))),
          const SizedBox(height: 8),
          TextButton(onPressed: onBack, child: const Text('Về danh sách bài học')),
        ]),
      ),
    );
  }
}

/// The bottom bar: the Check button, which turns into the feedback and Continue after an answer.
class _AnswerBar extends StatelessWidget {
  const _AnswerBar({required this.state, required this.typedText, required this.controller});

  final QuizState state;
  final String typedText;
  final QuizController controller;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final question = state.question!;
    final checked = state.phase == QuizPhase.feedback;
    final result = state.result;
    final correct = checked && result?.isCorrect == true;
    final wrong = checked && result != null && !result.isCorrect;
    final recorded = checked && state.recorded;

    final background = correct ? Colors.green.shade100 : wrong ? Colors.red.shade100 : recorded ? scheme.surfaceContainerHighest : scheme.surface;
    final canCheck = !state.busy && (question.isChoice ? state.selectedOptionId != null : typedText.trim().isNotEmpty);
    final buttonColor = correct ? Colors.green.shade700 : wrong ? Colors.red.shade700 : null;

    return AnimatedContainer(
      key: const Key('answer-bar'),
      duration: const Duration(milliseconds: 250),
      width: double.infinity,
      decoration: BoxDecoration(color: background, border: Border(top: BorderSide(color: scheme.outlineVariant))),
      child: SafeArea(
        top: false,
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 640),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
              child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                if (checked) _Feedback(state: state, correct: correct, wrong: wrong, recorded: recorded),
                if (state.error != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 10),
                    child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Icon(Icons.error_outline, color: scheme.error, size: 20),
                      const SizedBox(width: 8),
                      Expanded(child: Text(state.error!, key: const Key('answer-error'), style: TextStyle(color: scheme.error))),
                    ]),
                  ),
                FilledButton(
                  key: const Key('check-button'),
                  style: buttonColor == null ? null : FilledButton.styleFrom(backgroundColor: buttonColor, foregroundColor: Colors.white),
                  onPressed: checked ? (state.busy ? null : controller.next) : (canCheck ? () => controller.submit(typedText: typedText) : null),
                  child: state.busy
                      ? Row(mainAxisSize: MainAxisSize.min, children: [
                          const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)),
                          const SizedBox(width: 10),
                          Text(checked ? 'Đang hoàn tất...' : 'Đang kiểm tra...'),
                        ])
                      : Text(checked ? (state.isLast ? 'Hoàn thành' : 'Tiếp tục') : 'Kiểm tra'),
                ),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}

class _Feedback extends StatelessWidget {
  const _Feedback({required this.state, required this.correct, required this.wrong, required this.recorded});

  final QuizState state;
  final bool correct;
  final bool wrong;
  final bool recorded;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final result = state.result;
    final color = correct ? Colors.green.shade800 : wrong ? Colors.red.shade800 : theme.colorScheme.onSurfaceVariant;
    final icon = correct ? Icons.check_circle : wrong ? Icons.warning_amber_rounded : Icons.cloud_done;
    final title = correct ? _praise[state.index % _praise.length] : wrong ? 'Chưa đúng' : 'Đã ghi nhận câu trả lời';

    return Padding(
      key: const Key('feedback'),
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Icon(icon, size: 32, color: color),
        const SizedBox(width: 12),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title, style: theme.textTheme.titleMedium?.copyWith(color: color, fontWeight: FontWeight.w800)),
            if (recorded)
              const Text('Câu trả lời của bạn đã được lưu trước đó (có thể do kết nối bị gián đoạn). Hãy tiếp tục sang câu kế tiếp.'),
            if (wrong && result != null) ...[
              if (result.correctAnswer != null) _line('Đáp án đúng', result.correctAnswer!, theme, bold: true),
              if (result.explanation != null) _line('Giải thích', result.explanation!, theme),
              if (result.grammarNotes != null) _line('Lưu ý ngữ pháp', result.grammarNotes!, theme),
            ],
          ]),
        ),
      ]),
    );
  }

  Widget _line(String label, String value, ThemeData theme, {bool bold = false}) => Padding(
        padding: const EdgeInsets.only(top: 4),
        child: Text.rich(TextSpan(children: [
          TextSpan(text: '$label: ', style: const TextStyle(fontWeight: FontWeight.w700)),
          TextSpan(text: value, style: bold ? theme.textTheme.titleMedium : null),
        ])),
      );
}
