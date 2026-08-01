# 📚 Документация WinState

- [🖥️ Control Center](TERMINAL_UI.md)
- [🧩 Profile Engine](PROFILE_ENGINE.md)
- [🌿 Environment Provider](ENVIRONMENT_PROVIDER.md)
- [🏗️ Архитектура](ARCHITECTURE.md)
- [⚙️ Конфигурация](CONFIGURATION.md)
- [🗄️ SQLite-хранилище](STORAGE.md)
- [📝 Формат профиля](PROFILE_FORMAT.md)
- [🔌 Провайдеры](PROVIDERS.md)
- [🛡️ Безопасность](SECURITY.md)
- [🗺️ План реализации](IMPLEMENTATION_PLAN.md)

Текущий этап: `0.4.0-alpha.1` — первый реальный Windows Environment Provider с безопасным циклом `discover → plan → checkpoint → apply → verify → rollback`. Интерактивный Control Center и CLI используют один application workflow.
