import 'package:flutter/material.dart';

import '../data/quiz_models.dart';

const _typeLabels = {
  'MultipleChoice': 'Chọn đáp án đúng',
  'Translate': 'Dịch sang ngôn ngữ bạn đang học',
  'FillBlank': 'Điền vào chỗ trống',
  'Pronounce': 'Gõ lại từ hoặc câu này',
};

/// The question card: what is asked, and the way to answer (option cards or a text field).
class QuestionView extends StatelessWidget {
  const QuestionView({
    super.key,
    required this.question,
    required this.options,
    required this.selectedOptionId,
    required this.checked,
    required this.result,
    required this.textController,
    required this.onSelect,
    required this.onSubmitted,
  });

  final QuizQuestion question;

  /// The choices in the order they are shown.
  final List<QuizOption> options;
  final String? selectedOptionId;

  /// The answer was sent: the card is read-only and shows what was right.
  final bool checked;
  final AnswerResult? result;
  final TextEditingController textController;
  final ValueChanged<String> onSelect;
  final VoidCallback onSubmitted;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final typeLabel = _typeLabels[question.questionType] ?? question.questionType;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(typeLabel.toUpperCase(), style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.primary, fontWeight: FontWeight.w700, letterSpacing: 0.6)),
          const SizedBox(height: 8),
          if (question.isChoice) ..._choiceHeader(theme) else ..._typedHeader(theme),
          const SizedBox(height: 20),
          if (question.isChoice) _options(context) else _input(context),
        ]),
      ),
    );
  }

  /// The question is the target text; its translation (when there is one) is a hint under it.
  List<Widget> _choiceHeader(ThemeData theme) {
    final target = question.targetText ?? question.promptTranslation ?? '';
    return [
      Text(target, key: const Key('question-target'), style: theme.textTheme.headlineSmall),
      if (question.pinyinOrIpa != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(question.pinyinOrIpa!, style: theme.textTheme.bodyLarge?.copyWith(color: theme.colorScheme.outline))),
      if (question.promptTranslation != null && question.targetText != null)
        Padding(padding: const EdgeInsets.only(top: 8), child: Text('Gợi ý: ${question.promptTranslation}', style: theme.textTheme.bodyMedium)),
    ];
  }

  /// Typed answers: the target text is the answer, so the only thing shown is what to say, in the learner's language.
  List<Widget> _typedHeader(ThemeData theme) => [
        Text(question.promptTranslation ?? question.targetText ?? '', key: const Key('question-prompt'), style: theme.textTheme.titleLarge),
      ];

  Widget _options(BuildContext context) {
    return Column(children: [
      for (var i = 0; i < options.length; i++) ...[
        _OptionCard(
          key: Key('option-${options[i].id}'),
          number: i + 1,
          option: options[i],
          state: _stateOf(options[i]),
          onTap: checked ? null : () => onSelect(options[i].id),
        ),
        if (i < options.length - 1) const SizedBox(height: 10),
      ],
    ]);
  }

  _OptionState _stateOf(QuizOption option) {
    if (!checked) return option.id == selectedOptionId ? _OptionState.selected : _OptionState.idle;
    final correctId = result?.correctOptionId;
    if (result?.isCorrect == true && option.id == selectedOptionId) return _OptionState.correct;
    if (option.id == correctId) return _OptionState.correct;
    if (option.id == selectedOptionId && result != null) return _OptionState.wrong;
    return _OptionState.idle;
  }

  Widget _input(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final result = this.result;
    final color = !checked || result == null ? null : (result.isCorrect ? Colors.green.shade700 : Colors.red.shade700);
    return TextField(
      key: const Key('answer-input'),
      controller: textController,
      autofocus: true,
      readOnly: checked,
      textInputAction: TextInputAction.done,
      onSubmitted: (_) => onSubmitted(),
      autocorrect: false,
      enableSuggestions: false,
      maxLength: 500,
      style: Theme.of(context).textTheme.titleMedium,
      decoration: InputDecoration(
        hintText: 'Nhập câu trả lời',
        counterText: '',
        focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: color ?? scheme.primary, width: 2)),
        enabledBorder: color == null ? null : OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: color, width: 2)),
      ),
    );
  }
}

enum _OptionState { idle, selected, correct, wrong }

class _OptionCard extends StatelessWidget {
  const _OptionCard({super.key, required this.number, required this.option, required this.state, required this.onTap});

  final int number;
  final QuizOption option;
  final _OptionState state;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final (border, background, badge) = switch (state) {
      _OptionState.idle => (scheme.outlineVariant, scheme.surface, scheme.outline),
      _OptionState.selected => (scheme.primary, scheme.primaryContainer.withValues(alpha: 0.5), scheme.primary),
      _OptionState.correct => (Colors.green.shade600, Colors.green.shade50, Colors.green.shade600),
      _OptionState.wrong => (Colors.red.shade600, Colors.red.shade50, Colors.red.shade600),
    };
    final filled = state != _OptionState.idle;

    return Semantics(
      button: true,
      selected: state == _OptionState.selected,
      child: Material(
        color: background,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: border, width: 2)),
        child: InkWell(
          borderRadius: BorderRadius.circular(14),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            child: Row(children: [
              Container(
                width: 30,
                height: 30,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: filled ? badge : Colors.transparent,
                  borderRadius: BorderRadius.circular(9),
                  border: Border.all(color: badge, width: 2),
                ),
                child: Text('$number', style: theme.textTheme.labelLarge?.copyWith(color: filled ? Colors.white : badge, fontWeight: FontWeight.w700)),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text.rich(TextSpan(children: [
                  TextSpan(text: option.text, style: theme.textTheme.bodyLarge),
                  if (option.pinyinOrIpa != null) TextSpan(text: '  ${option.pinyinOrIpa}', style: theme.textTheme.bodySmall?.copyWith(color: scheme.outline)),
                ])),
              ),
            ]),
          ),
        ),
      ),
    );
  }
}
