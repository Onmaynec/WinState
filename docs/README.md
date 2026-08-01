# 📚 Документация WinState

- [🟢 Cyber Nexus / Control Center](CYBER_CONTROL_CENTER.md)
- [🧠 Unified Apply Engine](APPLY_ENGINE.md)
- [📡 Автоматическое обновление](AUTO_UPDATE.md)
- [🖥️ Terminal UI contracts](TERMINAL_UI.md)
- [🧩 Profile Engine](PROFILE_ENGINE.md)
- [🌿 Environment Provider](ENVIRONMENT_PROVIDER.md)
- [🏗️ Архитектура](ARCHITECTURE.md)
- [⚙️ Конфигурация](CONFIGURATION.md)
- [🗄️ SQLite-хранилище](STORAGE.md)
- [📝 Формат профиля](PROFILE_FORMAT.md)
- [🔌 Провайдеры](PROVIDERS.md)
- [🛡️ Безопасность](SECURITY.md)
- [🗺️ План реализации](IMPLEMENTATION_PLAN.md)

Текущий этап: `0.6.0-alpha.1` — Nexus Control Fabric, общий multi-provider Apply Engine и Update Uplink. Engine создаёт единый dependency graph, готовит checkpoints всех providers до первой мутации, сохраняет progress, поддерживает resume, reboot-pending state и cross-provider rollback. Update Uplink проверяет GitHub Releases, semantic version, ZIP и SHA-256; self-install доступен только официальной release-сборке.
