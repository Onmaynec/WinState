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

### Unified Apply Engine

- отдельный проект `WinState.Apply`;
- единый multi-provider execution graph;
- deterministic dependency ordering;
- missing dependency и cycle validation;
- centralized risk groups;
- admin/Critical/irreversible policy gates;
- checkpoint barrier всех providers;
- atomic persisted transaction manifest;
- progress persistence после каждого verified action;
- resume после остановки процесса;
- reboot-pending state;
- automatic/manual cross-provider rollback;
- Environment Provider adapter;
- Transaction Matrix в Nexus UI;
- fake multi-provider unit tests.

### Update Uplink

- отдельный проект `WinState.Update`;
- GitHub Releases API;
- stable/prerelease channels;
- Semantic Version comparison;
- startup check ledger;
- режимы `off/check/prompt/install`;
- runtime-specific ZIP selection;
- `.sha256` download и verification;
- safe extraction;
- release marker validation;
- отдельный updater process;
- self-contained `win-x64` и `win-arm64` packages;
- Windows package smoke test.

## ⏭️ Этап 7 — Packages и Windows Features (`0.7.0-alpha.1`)

- WinGet provider;
- package discovery и normalized identity;
- install/update/uninstall plan;
- ownership policy;
- safe remove-unmanaged mode;
- Windows Optional Features provider;
- WSL prerequisites;
- reboot grouping;
- подключение обоих providers к общему Apply Engine.

## Этап 8 — Services, Registry, Git, PowerShell и Files

- allowlisted registry provider;
- services/startup/tasks;
- Git configuration;
- PowerShell modules;
- managed files and directories.

## Этап 9 — Capture, drift и отчёты

- capture/export;
- profile snapshots;
- drift scan;
- transaction history UI;
- Markdown/JSON reports.

## Этап 10 — stable release pipeline

- localization;
- Authenticode signing;
- updater backup restore command;
- migration compatibility;
- release notes;
- stable `1.0` pipeline.
