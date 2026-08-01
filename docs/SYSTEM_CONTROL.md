# Windows System Control — WinState 0.8

Версия `0.8.0-alpha.1` добавляет provider `windows.system` поверх Unified Apply Engine.

Поддерживаются четыре типа ресурсов:

- allowlisted Registry values;
- существующие Windows Services;
- Startup entries через Registry Run;
- Scheduled Tasks через `schtasks.exe`.

## Профиль

```yaml
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
    state: present
    command: '"C:\\Tools\\agent.exe" --background'

tasks:
  - name: Example Maintenance
    state: present
    schedule: daily
    time: "03:30"
    runLevel: limited
    command: C:\\Tools\\maintain.exe
    arguments: --quiet
```

Готовый безопасный пример: `samples/system-control/safe-workstation.yaml`.

## Registry allowlist

WinState намеренно не предоставляет unrestricted Registry editor.

Разрешены только:

```text
HKCU\Software\...
HKLM\SOFTWARE\...
```

Поддерживаемые типы:

```text
string, expandString, dword, qword, multiString, binary
```

`multiString` задаётся JSON-массивом строк, `binary` — Base64.

Удаление Registry value имеет risk `High`. HKLM всегда требует administrator gate.

## Windows Services

WinState управляет только существующими services. Создание и удаление service definitions не выполняется.

Поддерживаемые состояния:

```text
state: running | stopped | unchanged
startMode: automatic | manual | disabled | unchanged
```

Остановка или отключение service имеет risk `High`. Все service actions требуют administrator gate.

## Startup

Startup entries хранятся в стандартном Registry Run key:

```text
HKCU/HKLM\Software\Microsoft\Windows\CurrentVersion\Run
```

Machine scope требует administrator gate. Перед созданием, изменением или удалением сохраняется прежнее значение.

## Scheduled Tasks

Поддерживаемые schedules:

```text
logon, startup, daily
```

Для `daily` требуется `time: HH:mm`. `runLevel: highest` и `schedule: startup` требуют administrator gate.

Для rollback сохраняется исходный XML задачи. Восстановление выполняется через `schtasks.exe /Create /XML`.

## Transaction safety

Все четыре ресурса используют общий pipeline:

```text
load → validate → targeted discovery → plan → policy gates
     → checkpoint barrier → apply → verification → persisted result
     → rollback / resume
```

Гарантии:

- системные процессы запускаются через `ProcessStartInfo.ArgumentList`;
- checkpoints создаются до первой мутации;
- success фиксируется только после повторного чтения состояния;
- destructive actions имеют повышенный risk;
- admin operations не получают скрытого elevation;
- Scheduled Tasks и Startup entries восстанавливаются из точного backup payload;
- profile `dependsOn` передаётся в общий dependency graph как action IDs.

## Ограничения alpha

- service definitions не создаются и не удаляются;
- Scheduled Task security descriptors не редактируются отдельно;
- Registry keys вне Software allowlist блокируются;
- dependency references пока указываются как точные action IDs;
- system-control секции загружаются отдельным совместимым loader поверх schema v1.
