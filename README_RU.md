<p align="center">
  <img src="assets/banner.svg" alt="WinState — декларативное управление Windows" width="100%" />
</p>

# 🇷🇺 WinState

Русская документация является основной документацией проекта. Полное описание текущей версии находится в [`README.md`](README.md).

## Текущая версия

**`0.6.0-alpha.1` — Nexus Control Fabric, Unified Apply Engine и безопасное автообновление.**

```text
Provider plans → unified execution graph
               → checkpoints → apply → verify
               → resume / reboot pending / rollback

GitHub Releases → Semantic Version
                → ZIP + SHA-256 → safe updater
```

## Быстрый старт

```powershell
git clone https://github.com/Onmaynec/WinState.git
cd WinState

dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/WinState.Cli
```

## Документация

- [`docs/CYBER_CONTROL_CENTER.md`](docs/CYBER_CONTROL_CENTER.md) — Nexus и cyber UI;
- [`docs/APPLY_ENGINE.md`](docs/APPLY_ENGINE.md) — общий transaction engine;
- [`docs/AUTO_UPDATE.md`](docs/AUTO_UPDATE.md) — проверка и установка обновлений;
- [`docs/PROFILE_ENGINE.md`](docs/PROFILE_ENGINE.md) — YAML Profile Engine;
- [`docs/ENVIRONMENT_PROVIDER.md`](docs/ENVIRONMENT_PROVIDER.md) — первый Windows provider;
- [`docs/SECURITY.md`](docs/SECURITY.md) — safeguards и threat model;
- [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) — roadmap.

## Автообновление

Официальная release-сборка проверяет GitHub Releases, сверяет Semantic Version, скачивает ZIP и `.sha256`, проверяет SHA-256 и устанавливает обновление отдельным процессом после завершения WinState.

Source checkout через `dotnet run` не перезаписывается. Для него обновление выполняется командой:

```powershell
git pull
```

## Лицензия

MIT License — см. [`LICENSE`](LICENSE).
