# 🗺️ План реализации WinState

## ✅ Этап 1 — архитектура (`0.1.0-alpha.1`)

- доменная модель, provider contracts и transactions;
- dependency graph;
- solution, тесты и CI.

## ✅ Этап 2 — каркас (`0.2.0-alpha.1`)

- DI, logging и configuration;
- portable/user-data paths;
- SQLite и миграции;
- Doctor, config и storage CLI.

## ✅ Этап 3 — Terminal UI и Profile Engine (`0.3.0-alpha.1`)

- стрелочный Control Center;
- полноценный YAML через YamlDotNet;
- includes, extends, variables и normalization;
- Profile Center.

## ✅ Этап 4 — Environment Provider (`0.4.0-alpha.1`)

```text
Discover → Diff → Plan → Confirm → Checkpoint → Apply → Verify → Rollback
```

- User/Machine variables и PATH;
- risk policy;
- checkpoint и verification;
- automatic/manual rollback;
- SQLite history;
- настоящий Windows CI vertical slice.

## ✅ Этап 5 — Cyber Control Center (`0.5.0-alpha.1`)

- NexRoute-inspired cyber UI;
- boot/shutdown traces;
- номерные channels;
- Profile Vault, Environment Ops, Checkpoint Vault;
- live telemetry, event feed и action traces;
- demo mode для CI.

## ✅ Этап 6 — Apply Engine и Update Uplink (`0.6.0-alpha.1`)

- `WinState.Apply` и единый multi-provider graph;
- risk/admin/Critical/irreversible policy gates;
- checkpoint barrier, persisted progress, resume и cross-provider rollback;
- reboot-pending state;
- Transaction Matrix;
- GitHub Releases, Semantic Version, SHA-256 и safe updater;
- self-contained `win-x64` / `win-arm64` packages.

## ✅ Этап 7 — Packages и Windows Features (`0.7.0-alpha.1`)

### WinGet Provider

- отдельный проект `WinState.Providers.Packages`;
- package discovery и normalized identity;
- exact-ID install, update и uninstall;
- exact/latest version policy;
- user/machine scope;
- install checkpoint и rollback;
- irreversible boundary для upgrade/uninstall;
- post-operation verification;
- fake-client unit tests.

### Windows Features Provider

- отдельный проект `WinState.Providers.Features`;
- DISM inventory;
- enable/disable через `/NoRestart`;
- administrator и reboot boundaries;
- checkpoint исходного состояния;
- verification и rollback;
- обработка exit code `3010`;
- fake-client unit tests.

### Интеграция

- YAML-секции `packages` и `features`;
- inheritance, variables, overlay и validation;
- три production adapters в одном Unified Apply Engine;
- Package & Feature Forge;
- provider telemetry и risk plan;
- sample, JSON Schema, документация и SVG-превью;
- Windows prerequisite scan для winget и DISM.

Отложено намеренно: массовый `removeUnmanagedPackages` и полноценная ownership policy. До появления надёжного ownership store WinState не удаляет неизвестные packages.

## ⏭️ Этап 8 — Registry, Services, Startup и Tasks (`0.8.0-alpha.1`)

- allowlisted Registry provider;
- Windows Services state/start mode;
- Startup entries;
- Scheduled Tasks definitions;
- ownership markers;
- per-resource backup payloads;
- dependency links между services/features/packages;
- verification и rollback через общий Apply Engine.

## Этап 9 — Git, PowerShell, Files и Capture

- Git configuration;
- PowerShell modules;
- managed files and directories;
- capture/export;
- profile snapshots и drift scan.

## Этап 10 — stable release pipeline

- localization;
- Authenticode signing;
- updater backup restore command;
- migration compatibility;
- Markdown/JSON reports;
- stable `1.0` pipeline.
