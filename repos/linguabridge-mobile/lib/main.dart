import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_web_plugins/url_strategy.dart';

import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';

void main() {
  // The STS redirects the browser to /callback?code=...; with the default hash strategy the app would never see that query.
  usePathUrlStrategy();
  runApp(const ProviderScope(child: LinguaBridgeApp()));
}

class LinguaBridgeApp extends ConsumerWidget {
  const LinguaBridgeApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return MaterialApp.router(
      title: 'LinguaBridge',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light,
      darkTheme: AppTheme.dark,
      routerConfig: ref.watch(appRouterProvider),
    );
  }
}
