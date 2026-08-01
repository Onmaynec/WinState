<p align="center">
  <img src="assets/banner.svg" alt="WinState — декларативное управление Windows" width="100%" />
</p>

<p align="center">
  <strong>Cyber-style консольная утилита для безопасного управления конфигурацией Windows как кодом.</strong>
</p>

<p align="center">
  <a href="docs/PACKAGES_FEATURES.md">📦 Packages & Features</a> ·
  <a href="docs/APPLY_ENGINE.md">🧠 Apply Engine</a> ·
  <a href="docs/AUTO_UPDATE.md">📡 Автообновление</a> ·
  <a href="docs/PROFILE_ENGINE.md">🧩 Profile Engine</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a>
</p>

---

## 🟢 WinState `0.7.0-alpha.1`

Версия `0.7` подключает к Unified Apply Engine два новых production providers:

```text
environment        → User/Machine variables и PATH
packages.winget    → install / upgrade / uninstall
windows.features   → DISM enable / disable
```

Все изменения проходят единый проверяемый конвейер:

```text
YAML profile → discovery → diff → unified execution graph
             → risk/admin/irreversible gates
             → checkpoints → apply → verification
             → persisted transaction → resume / rollback / reboot pending
```

## 🖥️ Превью Package & Feature Forge

<p align="center">
  <img src="assets/screenshots/package-feature-forge.svg" alt="WinState Package and Feature Forge" width="96%" />
</p>

## 🚀 Запуск

Требуется **.NET 8 SDK**.

```powershell
git clone https://github.com/Onmaynec/WinState.git
cd WinState

dotnet restore
dotnet build -c Release
dotnet test -c Release

dotnet run --project src/WinState.Cli
```

Без аргументов открывается **Forge Control Fabric**:

| Канал | Назначение |
|---|---|
| `[01] NEXUS CONTROL FABRIC` | Transaction Matrix, Update Uplink и прежний Cyber Control Center |
| `[02] PACKAGE & FEATURE FORGE` | WinGet inventory, DISM features, unified plan и execution trace |
| `[00] DISCONNECT` | безопасное завершение сессии |

## 📝 Профиль packages и features

```yaml
schemaVersion: 1

metadata:
  name: Developer Workstation

settings:
  allowReboot: false

packages:
  - id: Git.Git
    state: present
    version: latest
    source: winget
    scope: machine
    allowUpgrade: true
    mayRequireReboot: false

features:
  - name: Microsoft-Windows-Subsystem-Linux
    state: enabled
    includeParents: true

  - name: VirtualMachinePlatform
    state: enabled
    includeParents: true
```

Готовый пример: [`samples/packages-features/developer-workstation.yaml`](samples/packages-features/developer-workstation.yaml).

## 📦 WinGet Provider

Provider `packages.winget` использует официальный `winget.exe` и exact package ID.

| Сценарий | Action | Risk | Rollback |
|---|---|---|---|
| package отсутствует | `Install` | Low / Medium | ✅ удалить установленный транзакцией package |
| найдена другая версия | `Update` | Medium / High | ⚠️ не гарантируется |
| `state: absent` | `Uninstall` | High | ⚠️ не гарантируется |
| состояние совпадает | no-op | None | не требуется |

Upgrade и uninstall честно помечаются **irreversible**: нужная старая версия или installer могут исчезнуть из source. Unified Apply Engine не запустит такие действия без отдельного разрешения.

Команды запускаются без shell-конкатенации аргументов:

```text
--id <exact-id> --exact --silent --disable-interactivity
```

## 🧩 Windows Optional Features

Provider `windows.features` использует DISM:

```powershell
dism.exe /Online /Get-Features /Format:Table /English
dism.exe /Online /Enable-Feature  /FeatureName:<name> /NoRestart /Quiet /English /All
dism.exe /Online /Disable-Feature /FeatureName:<name> /NoRestart /Quiet /English
```

Правила безопасности:

- всегда требуется administrator policy;
- enable имеет risk `Medium`, disable — `High`;
- перед изменением сохраняется исходное состояние;
- rollback возвращает `Enabled`/`Disabled`;
- exit code `3010` переводится в reboot-pending;
- используется `/NoRestart`: WinState не перезагружает компьютер скрытно.

## 🧠 Unified Apply Engine

Три production adapters выполняются в одной транзакции:

```text
environment actions ─┐
winget actions ──────┼→ deterministic graph → risk groups → execution
feature actions ─────┘
```

Engine обеспечивает:

- проверку action IDs, dependencies и cycles;
- отдельные admin, Critical и irreversible policy gates;
- checkpoint barrier до первой мутации;
- verification каждого action;
- атомарный `transaction.json`;
- сохранение progress после каждого verified action;
- resume после остановки процесса;
- cross-provider rollback в обратном порядке;
- `SucceededRebootPending` без автоматической перезагрузки.

Manifest:

```text
<WINSTATE_HOME>/backups/transactions/<transaction-id>/transaction.json
```

Подробнее: [`docs/APPLY_ENGINE.md`](docs/APPLY_ENGINE.md).

## 📡 Автообновление

Официальная release-сборка проверяет GitHub Releases, выбирает `win-x64` или `win-arm64`, скачивает ZIP и `.sha256`, проверяет хеш, блокирует ZIP traversal и передаёт замену файлов отдельному updater-процессу.

```powershell
$env:WINSTATE_AUTO_UPDATE = "prompt"      # по умолчанию
$env:WINSTATE_UPDATE_CHANNEL = "prerelease"
.\winstate.exe
```

Запуск через `dotnet run` никогда не перезаписывает Git checkout.

Подробнее: [`docs/AUTO_UPDATE.md`](docs/AUTO_UPDATE.md).

## 🧩 Profile Engine

Поддерживаются:

- YAML `includes` и `extends`;
- обнаружение циклов;
- переменные `{{name}}`, `${name}`, `WINSTATE_VAR_*` и `--var`;
- overlay environment, packages и features;
- дедупликация package по `source + id`;
- дедупликация feature по имени;
- JSON Schema и русская validation diagnostics.

Проверка профиля:

```powershell
dotnet run --project src/WinState.Cli -- validate `
  .\samples\packages-features\developer-workstation.yaml
```

## 🧪 CI и тесты

GitHub Actions проверяет Ubuntu и Windows:

```text
restore → build with warnings-as-errors → all unit tests
        → version 0.7 assertion → Profile Engine samples
        → Forge demo → Environment status → Doctor → SQLite
```

На Windows дополнительно:

```text
winget prerequisite scan
DISM Optional Features inventory
real Environment plan → apply → verify → rollback
self-contained release ZIP + SHA-256 + marker smoke test
```

Provider unit-тесты используют `IWingetClient` и `IWindowsFeatureClient`, поэтому не устанавливают программы и не включают Windows features на машине разработчика.

## 🧱 Архитектура

```text
CyberForgeShell ─────────────── Update Uplink
       │                              │
       ▼                              ▼
WinState.App workflows          WinState.Update
       │
       ▼
WinState.Apply ── graph / manifest / resume / rollback
       │
       ├── EnvironmentApplyExecutor
       ├── WingetApplyExecutor
       └── WindowsFeatureApplyExecutor
                    │
        ┌───────────┼──────────────┐
        ▼           ▼              ▼
 Environment     WinGet          DISM Features
```

## 🛡️ Safety boundaries

- plan всегда строится до apply;
- все reversible checkpoints создаются до первой мутации;
- success возможен только после повторного discovery/verification;
- Machine/admin actions требуют отдельного подтверждения;
- package upgrade/uninstall требуют irreversible gate;
- automatic rollback включён по умолчанию;
- DISM запускается с `/NoRestart`;
- unmanaged packages не удаляются массово;
- updater проверяет SHA-256 и release marker;
- secrets не предназначены для обычного YAML-профиля.

## ⚠️ Ограничения alpha

- WinGet inventory зависит от формата современного App Installer;
- downgrade/rollback package upgrade не гарантируется;
- `removeUnmanagedPackages` пока не выполняет массовую очистку;
- полноценный reboot-resume после входа в Windows ещё не создаёт startup task;
- Authenticode signing запланирован на поздний release-этап;
- WinState не заменяет полный образ или backup Windows.

## 🗺️ Следующий этап

`0.8.0-alpha.1` — Registry, Services, Startup и Scheduled Tasks providers:

```text
allowlisted registry → service/startup state → task definitions
                     → ownership policy → unified verification/rollback
```

## 📦 Release package

```powershell
.\scripts\package.ps1 -Runtime win-x64 -Version 0.7.0-alpha.1
```

Результат:

```text
artifacts/WinState-0.7.0-alpha.1-win-x64.zip
artifacts/WinState-0.7.0-alpha.1-win-x64.zip.sha256
```

## 📄 Лицензия

MIT License — см. [`LICENSE`](LICENSE).
