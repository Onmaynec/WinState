<p align="center">
  <img src="assets/banner.svg" alt="WinState — декларативное управление Windows" width="100%" />
</p>

<p align="center">
  <strong>Cyber-style консольная утилита для безопасного декларативного управления Windows.</strong>
</p>

<p align="center">
  <a href="docs/CYBER_CONTROL_CENTER.md">🟢 Nexus UI</a> ·
  <a href="docs/APPLY_ENGINE.md">🧠 Apply Engine</a> ·
  <a href="docs/AUTO_UPDATE.md">📡 Автообновление</a> ·
  <a href="docs/PROFILE_ENGINE.md">🧩 Profile Engine</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a>
</p>

---

## 🟢 WinState `0.6.0-alpha.1`

Версия `0.6` добавляет два больших системных модуля:

1. **Unified Apply Engine** — единый transaction pipeline для нескольких providers.
2. **Update Uplink** — автоматическая проверка и безопасная установка актуального GitHub Release.

```text
profile providers → unified execution graph → risk groups
                  → all checkpoints → apply → verify
                  → resume / reboot pending / cross-provider rollback

GitHub Releases → semantic version → ZIP + SHA-256
                → safe staging → updater process → restart
```

> В `0.6` общий engine уже multi-provider, но первым реальным зарегистрированным adapter остаётся `Environment Provider`. Следующие providers смогут подключаться к тому же pipeline без копирования orchestration-кода.

## 🖥️ Превью Nexus Control Fabric

<p align="center">
  <img src="assets/screenshots/nexus-control-fabric.svg" alt="WinState Nexus Control Fabric" width="96%" />
</p>

## 🚀 Запуск из исходников

Требуется **.NET 8 SDK**.

```powershell
git clone https://github.com/Onmaynec/WinState.git
cd WinState

dotnet restore
dotnet build -c Release
dotnet test -c Release

dotnet run --project src/WinState.Cli
```

Без аргументов запускается интерактивный **Nexus Control Fabric**.

| Клавиша | Действие |
|---|---|
| `↑` / `↓` | выбрать operation channel |
| `Enter` | открыть канал |
| `Y/N` | подтвердить или заблокировать защищённую операцию |
| `Ctrl+C` | отменить сценарий; engine сохранит manifest и применит policy rollback |

## 🎛️ Верхний уровень Nexus

| Канал | Назначение |
|---|---|
| `[01] CYBER CONTROL CENTER` | прежние Profile Vault, Environment Ops, Deep Scan и Data Core |
| `[02] TRANSACTION MATRIX` | общий execution graph, risk groups, apply, resume и rollback |
| `[03] UPDATE UPLINK` | проверка GitHub Releases, SHA-256 и установка обновления |
| `[00] DISCONNECT` | безопасное завершение с shutdown trace |

## 🧠 Unified Apply Engine

Новый проект `WinState.Apply` не зависит от UI, YAML, SQLite или конкретного Windows API. Он получает готовые `PlannedAction` и provider executors.

### Execution graph

Engine:

- объединяет actions разных providers;
- проверяет уникальность action ID;
- проверяет отсутствующие dependencies;
- обнаруживает циклы;
- выполняет детерминированную topological sort;
- группирует действия по уровням риска;
- отдельно учитывает admin, reboot и irreversible actions.

### Transaction pipeline

```text
Build graph
→ validate policy
→ prepare rollback for every reversible action
→ atomically persist transaction.json
→ execute in dependency order
→ verify each action
→ persist progress after every verified action
→ finish or automatic cross-provider rollback
```

Manifest хранится здесь:

```text
<WINSTATE_HOME>/backups/transactions/<transaction-id>/transaction.json
```

Provider backups находятся внутри той же transaction directory:

```text
providers/<provider-id>/...
```

### Resume

Если процесс остановился между действиями, Transaction Matrix может загрузить manifest и продолжить сценарий. Уже подтверждённые действия пропускаются.

### Reboot pending

Если применённое действие требует перезагрузки, но автоматический reboot не разрешён, транзакция получает статус:

```text
SucceededRebootPending
```

WinState не перезагружает компьютер без отдельной будущей reboot policy.

### Cross-provider rollback

При ошибке или ручном откате engine проходит успешно применённые actions в обратном порядке и вызывает соответствующий provider executor.

## 📡 Автоматическое обновление

Update Uplink проверяет Releases репозитория `Onmaynec/WinState` при запуске не чаще одного раза в 6 часов.

Режим по умолчанию:

```text
check → показать новую версию → спросить разрешение
```

### Безопасный update pipeline

```text
GitHub Releases API
→ filter draft/channel
→ Semantic Version comparison
→ select matching win-x64 / win-arm64 ZIP
→ download ZIP and .sha256
→ verify SHA-256
→ safe extraction with path traversal protection
→ require winstate.release.json marker
→ start separate updater process
→ exit current WinState
→ backup installed files
→ replace files
→ restart winstate.exe
```

### Source checkout и release build

Самообновление файлов включается только у распакованной официальной release-сборки, где одновременно присутствуют:

```text
winstate.exe
winstate.release.json
```

Запуск через `dotnet run` **никогда не перезаписывает Git-репозиторий**. В source mode Update Uplink проверяет версию, но для обновления предлагает:

```powershell
git pull
```

### Настройки автообновления

| Переменная | Значения | По умолчанию |
|---|---|---|
| `WINSTATE_AUTO_UPDATE` | `off`, `check`, `prompt`, `install` | `prompt` |
| `WINSTATE_UPDATE_CHANNEL` | `stable`, `prerelease` | `prerelease` |
| `WINSTATE_UPDATE_INTERVAL_HOURS` | положительное число | `6` |
| `WINSTATE_UPDATE_TIMEOUT_SECONDS` | положительное число | `6` |
| `WINSTATE_UPDATE_RUNTIME` | `win-x64`, `win-arm64` | определяется автоматически |
| `WINSTATE_UPDATE_REPOSITORY` | `owner/repo` | `Onmaynec/WinState` |

Полностью автоматический режим:

```powershell
$env:WINSTATE_AUTO_UPDATE = "install"
.\winstate.exe
```

Отключение проверки:

```powershell
$env:WINSTATE_AUTO_UPDATE = "off"
```

Подробнее: [`docs/AUTO_UPDATE.md`](docs/AUTO_UPDATE.md).

## 🎞️ Анимации действий

Nexus показывает этапы реальных операций:

```text
HANDSHAKE → EXECUTE → SEAL RESULT
```

Анимации используются при:

- индексации transaction manifests;
- сборке unified graph;
- создании checkpoints;
- apply и verification;
- resume и rollback;
- подключении к GitHub Releases;
- загрузке и проверке update package;
- Profile Engine, Doctor и SQLite.

После применения выводится action trace с фактическими provider results, а не декоративными статусами.

## 🌿 Environment Provider

Первый adapter общего engine управляет:

- User/Machine environment variables;
- User/Machine `PATH` entries;
- create/modify;
- PATH add/remove/reorder;
- checkpoint;
- post-apply verification;
- automatic/manual rollback.

Существующие automation-команды сохранены:

```powershell
winstate environment status
winstate environment plan .\samples\environment-provider\user-sandbox.yaml
winstate environment apply .\samples\environment-provider\user-sandbox.yaml --yes
winstate environment checkpoints
winstate environment rollback <manifest.json> --yes
```

## 🧩 Profile Engine

Поддерживаются:

- `includes` и `extends`;
- защита от циклов;
- `{{name}}` и `${name}`;
- `WINSTATE_VAR_*`;
- `--var name=value`;
- объединение profile layers;
- нормализация и дедупликация PATH.

## 🧪 Проверки

GitHub Actions выполняет на Ubuntu и Windows:

```text
restore
→ build with warnings-as-errors
→ all unit tests
→ version assertion
→ Profile Engine smoke test
→ Nexus demo render
→ Environment status
→ Doctor and SQLite
```

Windows дополнительно выполняет:

```text
real User environment plan/apply/verify/rollback
→ assert variable and PATH restored

self-contained win-x64 publish
→ create ZIP and .sha256
→ extract package
→ assert winstate.exe and winstate.release.json
```

Apply Engine tests проверяют dependency order, risk groups, cycle detection, reboot-pending и rollback между двумя fake providers. Update tests проверяют semantic version и выбор stable/prerelease Releases без сетевых запросов.

## 🧱 Архитектура

```text
CyberNexusShell ─────────────── Update Uplink
       │                              │
       ▼                              ▼
WinState.App workflows          WinState.Update
       │
       ▼
WinState.Apply ── unified graph / manifest / resume / rollback
       │
       ├── EnvironmentApplyExecutor
       ▼
Provider implementations
       │
       ├── WinState.Providers.Environment
       ├── WinState.Storage
       ├── WinState.Core
       └── WinState.Domain
```

## 🛡️ Safety boundaries

- план всегда строится до изменения системы;
- все checkpoints создаются до первого apply;
- success возможен только после verification;
- elevated, Critical и irreversible groups требуют отдельного разрешения;
- automatic rollback включён по умолчанию;
- manifest записывается через temporary file и atomic replace;
- updater принимает только HTTPS GitHub Release assets;
- ZIP проверяется SHA-256;
- ZIP traversal блокируется;
- source checkout не перезаписывается;
- updater не изменяет `.winstate`, `profiles` и `logs`.

## ⚠️ Ограничения alpha

- реальный provider adapter пока один — Environment;
- resume продолжает действия, но полноценная reboot orchestration ещё не запускает продолжение после входа в Windows;
- package authenticity основана на GitHub HTTPS и SHA-256 asset; code signing запланирован позже;
- updater использует Windows PowerShell для замены занятых файлов после выхода процесса;
- WinState не заменяет полный образ или backup Windows.

## 🗺️ Следующий этап

`0.7.0-alpha.1` — Packages и Windows Features:

```text
WinGet Provider + Optional Features
→ ownership policy → unified Apply Engine
→ reboot planning → verified rollback boundaries
```

## 📦 Release package

```powershell
.\scripts\package.ps1 -Runtime win-x64 -Version 0.6.0-alpha.1
```

Создаются:

```text
artifacts/WinState-0.6.0-alpha.1-win-x64.zip
artifacts/WinState-0.6.0-alpha.1-win-x64.zip.sha256
```

## 📄 Лицензия

MIT License — см. [`LICENSE`](LICENSE).
