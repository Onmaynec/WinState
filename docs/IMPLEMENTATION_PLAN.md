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

- production providers `packages.winget` и `windows.features`;
- exact-ID package operations и DISM `/NoRestart`;
- package install rollback и честные irreversible boundaries;
- feature checkpoint, verification и rollback;
- YAML, samples, schema, Forge UI и Windows prerequisite CI.

## ✅ Этап 8 — Registry, Services, Startup и Tasks (`0.8.0-alpha.1`)

- production provider `windows.system`;
- Registry allowlist `HKCU\\Software` и `HKLM\\SOFTWARE`;
- Registry types string/expandString/dword/qword/multiString/binary;
- Windows Services state и start mode;
- Startup entries через стандартный Registry Run key;
- Scheduled Tasks `logon`, `startup` и `daily`;
- risk/admin policy для destructive и elevated operations;
- per-resource backup payloads, verification и rollback;
- includes/extends, variables и deterministic resource identities;
- подключение к общему Apply Engine и dependency graph;
- fake-client unit tests, sample, документация и SCM/Task Scheduler CI scan.

Persistent ownership store намеренно перенесён в этап Capture/Drift. В `0.8` WinState изменяет только явно перечисленные exact resources и не выполняет массовую очистку неизвестных Registry values, services, Startup entries или tasks.

## ⏭️ Этап 9 — Git, PowerShell, Files и Capture (`0.9.0-alpha.1`)

- Git configuration;
- PowerShell modules;
- managed files and directories;
- capture/export;
- persistent ownership markers;
- profile snapshots и drift scan.

## Этап 10 — stable release pipeline

- localization;
- Authenticode signing;
- updater backup restore command;
- migration compatibility;
- Markdown/JSON reports;
- stable `1.0` pipeline.
