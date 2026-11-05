import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

class _Destination {
  const _Destination(this.label, this.icon, this.selectedIcon);
  final String label;
  final IconData icon;
  final IconData selectedIcon;
}

const _destinations = [
  _Destination('Trang chủ', Icons.home_outlined, Icons.home),
  _Destination('Khóa học', Icons.menu_book_outlined, Icons.menu_book),
  _Destination('Ôn tập', Icons.style_outlined, Icons.style),
  _Destination('Gia sư AI', Icons.smart_toy_outlined, Icons.smart_toy),
];

/// The frame around the four tabs: a bottom bar on phones, a side rail on wide screens (tablet, desktop, browser).
class AppShell extends StatelessWidget {
  const AppShell({super.key, required this.shell});

  final StatefulNavigationShell shell;

  /// Tapping the tab that is already open returns to its first screen.
  void _select(int index) => shell.goBranch(index, initialLocation: index == shell.currentIndex);

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, constraints) {
      if (constraints.maxWidth >= 720) {
        final extended = constraints.maxWidth >= 1100;
        return Scaffold(
          body: Row(children: [
            NavigationRail(
              selectedIndex: shell.currentIndex,
              onDestinationSelected: _select,
              extended: extended,
              labelType: extended ? NavigationRailLabelType.none : NavigationRailLabelType.all,
              destinations: [
                for (final d in _destinations)
                  NavigationRailDestination(icon: Icon(d.icon), selectedIcon: Icon(d.selectedIcon), label: Text(d.label)),
              ],
            ),
            const VerticalDivider(width: 1),
            Expanded(child: shell),
          ]),
        );
      }
      return Scaffold(
        body: shell,
        bottomNavigationBar: NavigationBar(
          selectedIndex: shell.currentIndex,
          onDestinationSelected: _select,
          destinations: [
            for (final d in _destinations)
              NavigationDestination(icon: Icon(d.icon), selectedIcon: Icon(d.selectedIcon), label: d.label),
          ],
        ),
      );
    });
  }
}
