# 📝 Формат профиля WinState

Основной формат — человекочитаемый YAML со `schemaVersion: 1`.

```yaml
schemaVersion: 1

metadata:
  name: Developer Workstation
  description: Рабочая среда разработчика
  author: Example
  profileVersion: 1

settings:
  strictMode: false
  removeUnmanagedPackages: false
  allowReboot: false

variables:
  toolsRoot: C:\\Tools

extends:
  - base.yaml

includes:
  - company.yaml

environment:
  user:
    DEV_MODE: "true"
  machine:
    COMPANY_TOOLS: "{{toolsRoot}}"
  userPath:
    - path: "{{toolsRoot}}\\bin"
      state: present
      position: append

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

## 🪪 Metadata

| Поле | Назначение |
|---|---|
| `name` | обязательное имя профиля |
| `description` | описание назначения |
| `author` | автор или команда |
| `profileVersion` | версия самого профиля, минимум `1` |

## ⚙️ Settings

| Поле | Текущее поведение |
|---|---|
| `strictMode` | передаётся provider planning context |
| `removeUnmanagedPackages` | в `0.7` не удаляет неизвестные packages без ownership |
| `allowReboot` | описывает намерение профиля; скрытая перезагрузка не выполняется |

## 🧬 Includes и inheritance

`extends` и `includes` загружаются относительно файла-владельца. Profile Engine:

- обнаруживает cycles;
- объединяет baseline и overlay;
- разрешает variables после merge;
- выдаёт список всех source files.

Overlay rules:

```text
environment dictionaries → значение overlay побеждает
PATH entries             → объединение + дедупликация
packages                 → overlay по source + package ID
features                 → overlay по FeatureName
```

## 🧩 Variables

Поддерживаются формы:

```yaml
"{{name}}"
"${name}"
```

Приоритет:

```text
profile variables
→ WINSTATE_VAR_<name>
→ CLI --var name=value
```

Встроенные variables:

```text
profileFile
profileDirectory
```

## 🌿 Environment

```yaml
environment:
  user:
    DEV_MODE: "true"
  machine:
    COMPANY_MODE: "managed"
  userPath:
    - path: .\tools\bin
      state: present
      position: prepend
  machinePath:
    - path: C:\\Company\\bin
      state: absent
      position: append
```

PATH state: `present` или `absent`. Position: `prepend` или `append`.

## 📦 Packages

```yaml
packages:
  - id: Microsoft.PowerShell
    state: present
    version: latest
    source: winget
    scope: machine
    allowUpgrade: true
    mayRequireReboot: false
```

| Поле | Значения | Default |
|---|---|---|
| `id` | exact WinGet package ID | обязательное |
| `state` | `present`, `absent` | `present` |
| `version` | `latest` или exact version | `latest` |
| `source` | WinGet source name | `winget` |
| `scope` | `user`, `machine` | `user` |
| `allowUpgrade` | boolean | `true` |
| `mayRequireReboot` | boolean | `false` |

Новая установка может иметь rollback. Upgrade/uninstall считаются irreversible и требуют отдельного policy gate.

## 🧱 Features

```yaml
features:
  - name: VirtualMachinePlatform
    state: enabled
    includeParents: true
```

| Поле | Значения | Default |
|---|---|---|
| `name` | exact DISM FeatureName | обязательное |
| `state` | `enabled`, `disabled` | `enabled` |
| `includeParents` | boolean; соответствует `/All` при enable | `true` |

Все feature actions требуют administrator policy и могут перевести транзакцию в reboot-pending.

## 🔐 Правила безопасности

- secrets не записываются напрямую;
- неизвестные packages не удаляются массово;
- package/feature values не выполняются как команды;
- WinGet получает exact ID и отдельные process arguments;
- DISM принимает только поддерживаемые фиксированные операции;
- cycles запрещены;
- apply возможен только после validation, plan и confirmations;
- success фиксируется только после verification.

JSON Schema: [`schemas/winstate.schema.json`](../schemas/winstate.schema.json).

Полный пример: [`samples/packages-features/developer-workstation.yaml`](../samples/packages-features/developer-workstation.yaml).
