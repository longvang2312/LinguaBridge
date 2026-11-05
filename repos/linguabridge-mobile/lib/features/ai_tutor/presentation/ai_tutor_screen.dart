import 'package:flutter/material.dart';

/// Chat with the AI tutor. A placeholder conversation until the AI endpoints are wired in.
class AiTutorScreen extends StatelessWidget {
  const AiTutorScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Gia sư AI')),
      body: Column(children: [
        Expanded(
          child: ListView(padding: const EdgeInsets.all(16), children: [
            Align(
              alignment: Alignment.centerLeft,
              child: Container(
                constraints: const BoxConstraints(maxWidth: 320),
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(color: theme.colorScheme.surfaceContainerHighest, borderRadius: BorderRadius.circular(16)),
                child: const Text('Xin chào! Mình là gia sư AI của bạn. Hãy chọn một tình huống để bắt đầu luyện nói.'),
              ),
            ),
          ]),
        ),
        const SafeArea(
          top: false,
          child: Padding(
            padding: EdgeInsets.fromLTRB(16, 8, 16, 12),
            child: Row(children: [
              Expanded(child: TextField(enabled: false, decoration: InputDecoration(hintText: 'Nhập tin nhắn (sắp có)'))),
              SizedBox(width: 8),
              IconButton.filled(onPressed: null, icon: Icon(Icons.send)),
            ]),
          ),
        ),
      ]),
    );
  }
}
