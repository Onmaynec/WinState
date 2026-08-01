# 📚 Документация WinState

- [📦 WinGet Packages и Windows Features](PACKAGES_FEATURES.md)
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

Текущий этап: `0.7.0-alpha.1` — Package & Feature Forge и три production providers в одном Unified Apply Engine:

```text
environment + packages.winget + windows.features
→ unified dependency graph → policy gates → checkpoints
→ apply → verification → resume / rollback / reboot pending
```

WinGet upgrade/uninstall честно помечаются irreversible. Optional Features используют DISM с `/NoRestart`, сохраняют исходное состояние и требуют administrator policy.
