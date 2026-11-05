import 'package:flutter/material.dart';

/// LinguaBridge brand colours and the light / dark themes built from them.
abstract final class AppTheme {
  static const primary = Color(0xFF2563EB);
  static const accent = Color(0xFFF59F00);

  static ThemeData get light => _build(Brightness.light);
  static ThemeData get dark => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final isLight = brightness == Brightness.light;
    // fromSeed would shift the seed's tone; the brand blue is used as it is (a lighter tint of it on dark surfaces).
    final scheme = ColorScheme.fromSeed(seedColor: primary, brightness: brightness).copyWith(
      primary: isLight ? primary : const Color(0xFF93B4FF),
      onPrimary: isLight ? Colors.white : const Color(0xFF0B1F52),
    );
    final base = ThemeData(useMaterial3: true, colorScheme: scheme);
    return base.copyWith(
      scaffoldBackgroundColor: isLight ? const Color(0xFFF4F6FB) : null,
      appBarTheme: AppBarTheme(
        centerTitle: false,
        backgroundColor: isLight ? primary : scheme.surface,
        foregroundColor: isLight ? Colors.white : scheme.onSurface,
        elevation: 0,
        titleTextStyle: base.textTheme.titleLarge?.copyWith(
          fontWeight: FontWeight.w700,
          color: isLight ? Colors.white : scheme.onSurface,
        ),
      ),
      cardTheme: CardThemeData(
        elevation: 1,
        margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(12)),
      ),
      textTheme: base.textTheme.copyWith(
        headlineSmall: base.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700),
        titleLarge: base.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
        titleMedium: base.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
      ),
    );
  }
}
