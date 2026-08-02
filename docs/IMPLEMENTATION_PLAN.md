# 🗺️ План реализации WinState

## ✅ Этапы 1–5 — фундамент (`0.1`–`0.5`)

- доменная модель, provider contracts и dependency graph;
- DI, конфигурация, SQLite и диагностика;
- YAML Profile Engine с includes, extends и variables;
- Environment Provider с checkpoint, verification и rollback;
- интерактивный Control Center и CI demo mode.

## ✅ Этап 6 — Apply Engine и Update Uplink (`0.6.0-alpha.1`)

- единый multi-provider execution graph;
- admin/Critical/irreversible policy gates;
- checkpoint barrier, persisted progress, resume и cross-provider rollback;
- GitHub Releases, Semantic Version, SHA-256 и безопасный updater;
- self-contained `win-x64` / `win-arm64` packages.

## ✅ Этап 7 — Packages и Windows Features (`0.7.0-alpha.1`)

- production providers `packages.winget` и `windows.features`;
- exact-ID package operations;
- DISM `/NoRestart`;
- package/feature verification и rollback;
- samples, schema, Forge UI и Windows CI.

## ✅ Этап 8 — Registry, Services, Startup и Tasks (`0.8.0-alpha.1`)

- production provider `windows.system`;
- Registry allowlist `HKCU\\Software` и `HKLM\\SOFTWARE`;
- Windows Services state/start mode;
- Startup entries и Scheduled Tasks;
- per-resource backup payloads, verification и rollback;
- SCM/Task Scheduler prerequisite CI.

## ✅ Этап 9 — Capture и Drift (`0.9.0`)

- первый стабильный релиз без prerelease-суффикса;
- безопасный экспорт текущего состояния в YAML;
- JSON-манифест снимка и SHA-256;
- environment, PATH, WinGet packages и enabled Optional Features;
- фильтрация потенциальных секретов;
- read-only drift scan через Unified Apply Plan;
- стабильные exit codes и Windows vertical slice;
- русская CLI, документация и GitHub Release notes;
- stable releases автоматически становятся `Latest`.

## ✅ Этап 10 — Workspace Control и Recovery (`1.0.0`)

- Git global configuration provider;
- PowerShell modules provider для `CurrentUser`;
- managed UTF-8 files и directories;
- JSON Workspace manifest schema version 1;
- persistent versioned ownership ledger;
- блокировка удаления неизвестных ресурсов;
- отдельные gates для module install и deletion;
- persisted plan/apply transactions;
- automatic и manual rollback;
- JSON и Markdown reports;
- updater backup restore с safety backup;
- сохранение `.winstate`, `profiles` и `logs` при recovery;
- migration compatibility policy;
- Ubuntu/Windows Workspace vertical slice;
- условный Authenticode signing stage;
- stable `1.0.0` release pipeline.

## Следующие направления после 1.0

- YAML-представление Workspace manifest;
- расширенная работа с PowerShell repositories и offline module cache;
- managed file templates с безопасным secret injection;
- signed provenance/SBOM для release assets;
- расширение UI разделом Workspace Control;
- remote fleet inventory и централизованные drift reports;
- дополнительные Windows providers без ослабления ownership/risk gates.
