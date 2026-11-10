import 'package:flutter/material.dart';

import '../data/quiz_models.dart';

/// The lesson is done: how it went and what to do next.
class ResultView extends StatelessWidget {
  const ResultView({super.key, required this.outcome, required this.busy, required this.onBack, required this.onAgain});

  final AttemptResult outcome;
  final bool busy;
  final VoidCallback onBack;
  final VoidCallback onAgain;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final score = outcome.scorePercentage;
    final (icon, color, title) = score >= 80
        ? (Icons.emoji_events, Colors.amber.shade700, 'Xuất sắc!')
        : score >= 50
            ? (Icons.star, Colors.amber.shade600, 'Hoàn thành bài học!')
            : (Icons.fitness_center, theme.colorScheme.primary, 'Cố gắng thêm nhé!');
    final progress = outcome.progress;

    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(24),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 480),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            TweenAnimationBuilder<double>(
              tween: Tween(begin: 0.3, end: 1),
              duration: const Duration(milliseconds: 500),
              curve: Curves.elasticOut,
              builder: (context, scale, child) => Transform.scale(scale: scale, child: child),
              child: Icon(icon, size: 88, color: color),
            ),
            const SizedBox(height: 12),
            Text(title, textAlign: TextAlign.center, style: theme.textTheme.headlineMedium?.copyWith(fontWeight: FontWeight.w800)),
            const SizedBox(height: 20),
            Row(children: [
              Expanded(child: _Stat(key: const Key('stat-score'), label: 'Chính xác', value: '$score%', color: theme.colorScheme.primary)),
              const SizedBox(width: 12),
              Expanded(child: _Stat(key: const Key('stat-correct'), label: 'Câu đúng', value: '${outcome.correctCount}/${outcome.totalQuestions}', color: Colors.green.shade700)),
              const SizedBox(width: 12),
              Expanded(child: _Stat(key: const Key('stat-xp'), label: 'XP nhận được', value: '${outcome.xpEarned}', color: Colors.orange.shade800, icon: Icons.bolt)),
            ]),
            if (!outcome.isFirstCompletion) ...[
              const SizedBox(height: 12),
              Text('Bạn đã hoàn thành bài học này trước đó nên lần luyện tập này không được thêm XP.', textAlign: TextAlign.center, style: theme.textTheme.bodyMedium),
            ],
            if (progress != null) ...[
              const SizedBox(height: 12),
              Text(
                'Tiến độ khóa học: ${progress.completedLessons}/${progress.totalLessons} bài học',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium,
              ),
              if (progress.isCompleted)
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text('Bạn đã hoàn thành cả khóa học!', textAlign: TextAlign.center, style: theme.textTheme.titleSmall?.copyWith(color: Colors.green.shade700)),
                ),
            ],
            const SizedBox(height: 28),
            FilledButton.icon(onPressed: busy ? null : onBack, icon: const Icon(Icons.list_alt), label: const Text('Về danh sách bài học')),
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: busy ? null : onAgain,
              icon: const Icon(Icons.replay),
              label: const Text('Làm lại bài này'),
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
            ),
          ]),
        ),
      ),
    );
  }
}

class _Stat extends StatelessWidget {
  const _Stat({super.key, required this.label, required this.value, required this.color, this.icon});

  final String label;
  final String value;
  final Color color;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 14, horizontal: 8),
        child: Column(children: [
          // Shrinks instead of overflowing on a narrow phone.
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
              if (icon != null) Icon(icon, color: color, size: 22),
              Text(value, style: theme.textTheme.headlineSmall?.copyWith(color: color, fontWeight: FontWeight.w800)),
            ]),
          ),
          const SizedBox(height: 2),
          Text(label, textAlign: TextAlign.center, style: theme.textTheme.bodySmall),
        ]),
      ),
    );
  }
}
