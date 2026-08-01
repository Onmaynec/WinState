<p align="center">
  <img src="assets/banner.svg" alt="WinState — декларативное управление Windows" width="100%" />
</p>

<p align="center">
  <strong>Cyber-style консольная утилита для безопасного управления конфигурацией Windows как кодом.</strong>
</p>

<p align="center">
  <a href="docs/SYSTEM_CONTROL.md">🪟 System Control</a> ·
  <a href="docs/PACKAGES_FEATURES.md">📦 Packages & Features</a> ·
  <a href="docs/APPLY_ENGINE.md">🧠 Apply Engine</a> ·
  <a href="docs/AUTO_UPDATE.md">📡 Автообновление</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a>
</p>

---

## 🟢 WinState `0.8.0-alpha.1`

Версия `0.8` добавляет **Windows System Control Plane**:

```text
environment        → User/Machine variables и PATH
packages.winget    → install / upgrade / uninstall
windows.features   → DISM enable / disable
windows.system     → Registry / Services / Startup / Scheduled Tasks
```

Все providers выполняются через единый проверяемый pipeline:

```text
YAML profile → discovery → deterministic graph → risk/admin gates
             → all checkpoints → apply → verification
             → persisted transaction → resume / rollback
```

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

## 🪟 System Control profile

```yaml
schemaVersion: 1

metadata:
  name: Managed Workstation

registry:
  - hive: HKCU
    path: Software\\Example\\App
    name: Channel
    state: present
    type: string
    value: stable

services:
  - name: EventLog
    state: running
    startMode: automatic

startup:
  - name: Example Agent
    scope: user
    state: absent

tasks:
  - name: Example Maintenance
    state: absent
    schedule: logon
    runLevel: limited
```

Готовый пример: [`samples/system-control/safe-workstation.yaml`](samples/system-control/safe-workstation.yaml).

Подробнее: [`docs/SYSTEM_CONTROL.md`](docs/SYSTEM_CONTROL.md).

## Registry safety

WinState не является unrestricted Registry editor. Разрешены только:

```text
HKCU\Software\...
HKLM\SOFTWARE\...
```

Поддерживаются `string`, `expandString`, `dword`, `qword`, `multiString` и `binary`. Удаление value имеет risk `High`; HKLM требует administrator gate.

## Windows Services

Provider управляет только существующими services:

```text
state: running | stopped | unchanged
startMode: automatic | manual | disabled | unchanged
```

Остановка или отключение service имеет risk `High`. Создание и удаление service definitions не выполняется.

## Startup и Scheduled Tasks

Startup entries используют стандартный Registry Run key. Scheduled Tasks создаются через `schtasks.exe` и поддерживают schedules `logon`, `startup`, `daily`.

Для rollback сохраняются:

- прежнее Registry value;
- исходные service state/start mode;
- прежняя Startup command;
- полный XML Scheduled Task.

## 📦 Packages и Windows Features

`packages.winget` использует exact package IDs и `ProcessStartInfo.ArgumentList`. Upgrade/uninstall считаются irreversible, если точное восстановление версии не гарантируется.

`windows.features` использует DISM с `/NoRestart`; exit code `3010` переводится в reboot-pending без скрытой перезагрузки.

Подробнее: [`docs/PACKAGES_FEATURES.md`](docs/PACKAGES_FEATURES.md).

## 🧠 Unified Apply Engine

Engine обеспечивает:

- deterministic dependency ordering;
- проверку missing dependencies и cycles;
- отдельные admin, Critical и irreversible gates;
- checkpoint barrier до первой мутации;
- verification каждого action;
- атомарный `transaction.json`;
- progress persistence и resume;
- cross-provider rollback в обратном порядке.

Подробнее: [`docs/APPLY_ENGINE.md`](docs/APPLY_ENGINE.md).

## 📡 Автообновление и релизы

Tag workflow собирает self-contained пакеты:

```text
WinState-0.8.0-alpha.1-win-x64.zip
WinState-0.8.0-alpha.1-win-x64.zip.sha256
WinState-0.8.0-alpha.1-win-arm64.zip
WinState-0.8.0-alpha.1-win-arm64.zip.sha256
```

Каждый ZIP содержит `winstate.exe` и `winstate.release.json`. Updater проверяет SHA-256 и блокирует ZIP path traversal.

```powershell
.\scripts\package.ps1 -Runtime win-x64 -Version 0.8.0-alpha.1
```

## 🧪 CI

GitHub Actions проверяет Ubuntu и Windows:

```text
restore → warnings-as-errors build → all tests
        → version 0.8 assertion → all profile samples
        → Forge demo/provider registration
        → Windows winget/DISM/SCM/Task Scheduler prerequisites
        → release package + SHA-256 smoke
```

System Control unit-тесты используют `IWindowsSystemClient`, поэтому CI не меняет реальные Registry values, services, Startup entries или tasks.

## 🛡️ Safety boundaries

- plan всегда строится до apply;
- all reversible checkpoints создаются до первой мутации;
- success возможен только после повторного verification;
- Machine/admin actions требуют отдельного policy gate;
- destructive actions получают `High` risk;
- Registry ограничен Software allowlist;
- автоматического elevation или reboot нет;
- unmanaged packages и неизвестные system resources массово не удаляются.

## ⚠️ Ограничения alpha

- service definitions не создаются и не удаляются;
- WinGet inventory зависит от формата App Installer;
- downgrade после package upgrade не гарантируется;
- dependency references для System Control пока задаются точными action IDs;
- Authenticode signing запланирован на stable pipeline.

## 🗺️ Следующий этап

`0.9.0-alpha.1` — Git configuration, PowerShell modules, managed files/directories, capture/export и drift scan.

## 📄 Лицензия

MIT License — см. [`LICENSE`](LICENSE).
