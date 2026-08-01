# 📦 WinGet Packages и Windows Optional Features

WinState `0.7.0-alpha.1` добавляет два production providers поверх общего `WinState.Apply`:

```text
packages.winget  → winget.exe
windows.features → dism.exe
```

Оба provider проходят один safety pipeline:

```text
Profile → Discovery → Diff → Unified graph → Policy gates
        → All checkpoints → Apply → Verify → Persist result
        → Resume / reboot pending / cross-provider rollback
```

## 📝 Формат профиля

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
```

## 📦 WinGet Provider

Provider ID:

```text
packages.winget
```

Поддерживаются операции:

| Состояние | Текущее состояние | План |
|---|---|---|
| `present` | package отсутствует | `Install` |
| `present`, exact version | версия отличается | `Update` |
| `present`, `latest` | WinGet показывает update | `Update` |
| `absent` | package установлен | `Uninstall` |
| состояние совпадает | — | пустой план |

### Поля package

| Поле | Назначение | По умолчанию |
|---|---|---|
| `id` | exact WinGet package ID | обязательное |
| `state` | `present` / `absent` | `present` |
| `version` | `latest` или точная версия | `latest` |
| `source` | источник WinGet | `winget` |
| `scope` | `user` / `machine` | `user` |
| `allowUpgrade` | разрешить update при `latest` | `true` |
| `mayRequireReboot` | отметить reboot boundary | `false` |

### Rollback boundary

WinState не обещает невозможного:

- новая установка имеет checkpoint и может быть удалена при rollback;
- upgrade помечается как **irreversible**, потому что старая версия может исчезнуть из source;
- uninstall помечается как **irreversible**, потому что точная версия и installer могут быть недоступны;
- irreversible actions требуют отдельного policy confirmation.

```text
Install   → rollback: uninstall newly installed package
Upgrade   → no guaranteed rollback
Uninstall → no guaranteed rollback
```

WinGet запускается через `ProcessStartInfo.ArgumentList`, без shell-конкатенации аргументов. Используются exact ID, silent mode, source/package agreements и disabled interactivity.

## 🧩 Windows Features Provider

Provider ID:

```text
windows.features
```

Discovery выполняется командой:

```powershell
dism.exe /Online /Get-Features /Format:Table /English
```

Изменения:

```powershell
dism.exe /Online /Enable-Feature  /FeatureName:<name> /NoRestart /Quiet /English /All
dism.exe /Online /Disable-Feature /FeatureName:<name> /NoRestart /Quiet /English
```

### Поля feature

| Поле | Назначение | По умолчанию |
|---|---|---|
| `name` | точное DISM FeatureName | обязательное |
| `state` | `enabled` / `disabled` | `enabled` |
| `includeParents` | включить parent dependencies через `/All` | `true` |

### Safety и rollback

- все feature actions требуют administrator policy;
- enable получает risk `Medium`;
- disable получает risk `High`;
- каждое действие отмечает возможную перезагрузку;
- DISM всегда вызывается с `/NoRestart`;
- checkpoint сохраняет исходное Enabled/Disabled state;
- rollback возвращает исходное состояние;
- код `3010` фиксируется как success + reboot required;
- WinState не перезагружает компьютер автоматически.

## 🧠 Unified graph

Environment, packages и features объединяются в одну транзакцию:

```text
environment actions ─┐
winget actions ──────┼→ deterministic graph → risk groups → execution
feature actions ─────┘
```

До первой мутации engine подготавливает все доступные checkpoints. Если checkpoint обратимого action создать невозможно, транзакция не запускается.

Пример итогового плана:

```text
[LOW]    packages.winget   Install   Git.Git
[MEDIUM] environment       Modify    machine variable
[MEDIUM] windows.features  Enable    VirtualMachinePlatform
[HIGH]   packages.winget   Update    Microsoft.PowerShell (irreversible)
```

## 🖥️ Package & Feature Forge

Интерактивный frontend `CyberForgeShell` добавляет каналы:

```text
[01] NEXUS CONTROL FABRIC
[02] PACKAGE & FEATURE FORGE
[00] DISCONNECT
```

Forge показывает:

- доступность Environment, WinGet и Features providers;
- число установленных packages;
- число найденных updates;
- enabled/disabled feature counters;
- provider diagnostics;
- unified plan и risk groups;
- administrator/reboot/irreversible gates;
- настоящий execution trace после verification.

## 🧪 Тестирование

Unit-тесты не изменяют Windows. Providers получают интерфейсы:

```text
IWingetClient
IWindowsFeatureClient
```

Тестовые fake clients проверяют:

- install plan и rollback capability;
- upgrade как irreversible action;
- package checkpoint → apply → verify → rollback;
- feature enable risk/admin/reboot flags;
- feature checkpoint → apply → verify → rollback;
- unknown feature как unsupported action;
- inheritance и overlay секций packages/features.

Windows CI дополнительно проверяет наличие `winget` и чтение DISM inventory, но не устанавливает packages и не включает features на runner.

## ⚠️ Ограничения alpha

- парсер `winget list` опирается на табличные колонки и требует современный App Installer;
- package upgrade/uninstall не имеют гарантированного rollback;
- Optional Features изменяются только elevated-процессом;
- `removeUnmanagedPackages` пока не выполняет массовое удаление;
- ownership database для packages будет расширена в последующих версиях;
- WinState не запускает автоматическую перезагрузку.
