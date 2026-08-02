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

- стабильный релиз без prerelease-суффикса;
- безопасный экспорт текущего состояния в YAML;
- JSON-манифест снимка и SHA-256;
- экспорт environment, PATH, WinGet packages и enabled Optional Features;
- фильтрация потенциальных секретов;
- read-only drift scan через Unified Apply Plan;
- JSON-отчёт действий, рисков, admin requirements и rollback capability;
- стабильные exit codes для CI;
- Windows vertical slice `capture → validate → drift`;
- русская CLI-справка, документация и GitHub Release notes;
- стабильные релизы автоматически помечаются `Latest`.

Git configuration, PowerShell modules и managed files/directories перенесены в следующий этап, чтобы не смешивать их с первым стабильным наблюдаемым циклом Capture/Drift.

## ⏭️ Этап 10 — WinState 1.0

- Git configuration provider;
- PowerShell modules provider;
- managed files and directories;
- persistent ownership store;
- migration compatibility policy;
- updater backup restore command;
- Markdown reports;
- Authenticode signing;
- локализация UI без смешения языков;
- стабильный `1.0` release pipeline.
