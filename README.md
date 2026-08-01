<p align="center">
  <img src="assets/banner.svg" alt="WinState — Git для конфигурации Windows" width="100%" />
</p>

<p align="center">
  <strong>Cyber-style консольная утилита для безопасного декларативного управления Windows.</strong>
</p>

<p align="center">
  <a href="docs/CYBER_CONTROL_CENTER.md">🟢 Cyber Control Center</a> ·
  <a href="docs/PROFILE_ENGINE.md">🧩 Profile Engine</a> ·
  <a href="docs/ENVIRONMENT_PROVIDER.md">🌿 Environment Provider</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a> ·
  <a href="docs/IMPLEMENTATION_PLAN.md">🗺️ Roadmap</a>
</p>

---

## 🟢 WinState `0.5.0-alpha.1`

WinState получил полностью новый интерактивный frontend в стиле отдельной hacker/cyber Windows-утилиты. Интерфейс стал ближе к визуальному языку **NexRoute**: плотный Control Node, номерные operation channels, boot trace, зелёная high-contrast палитра, живые статусы, action streams и анимации реальных операций.

```text
boot trace → control node → operation channel
           → animated pipeline → live action trace → verified result
```

Визуальный апгрейд не меняет safety boundaries. Все изменения Windows по-прежнему выполняются только через application workflow:

```text
Discover → Diff → Plan → Confirm
         → Checkpoint → Apply → Verify → Rollback
```

## 🖥️ Превью Cyber Control Center

<p align="center">
  <img src="assets/screenshots/cyber-control-center.svg" alt="WinState Cyber Control Center" width="96%" />
</p>

> Превью схематически показывает интерфейс. Реальный вид зависит от терминала, шрифта, ширины окна и поддержки ANSI-цветов.

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

Без аргументов запускается интерактивный Cyber Control Center.

| Клавиша | Действие |
|---|---|
| `↑` / `↓` | перемещение между operation channels |
| `Enter` | открыть выбранный канал |
| `Y/N` | подтвердить или отменить защищённую операцию |
| `Ctrl+C` | безопасно прервать текущий сценарий |

## 🎛️ Operation channels

| Канал | Раздел | Что показывает |
|---|---|---|
| `[01]` | **Control Node** | live telemetry, состояние providers, counters и event feed |
| `[02]` | **Profile Vault** | YAML-профили, includes, variables и validation |
| `[03]` | **Environment Ops** | plan, apply, verification и automatic rollback |
| `[04]` | **Checkpoint Vault** | сохранённые manifest и ручное восстановление |
| `[05]` | **Deep Scan** | анимированная диагностика модулей |
| `[06]` | **Data Core** | SQLite schema, migrations и таблицы |
| `[07]` | **Node Config** | каталоги, режим и runtime settings |
| `[08]` | **System Map** | архитектура, safeguards и roadmap |
| `[00]` | **Disconnect** | анимированное завершение сессии |

## 🎞️ Анимации действий

Каждая продолжительная операция отображается как pipeline:

```text
handshake → operation → seal result
```

Анимации используются для:

- запуска Data Core;
- загрузки и проверки профиля;
- discovery и построения diff;
- checkpoint/apply/verify transaction;
- rollback;
- Doctor scan;
- проверки SQLite migrations.

После транзакции интерфейс выводит поток реальных action results:

```text
11:45:27.132 PASS          env-create-1a2b3c // Переменная подтверждена.
11:45:27.208 PASS          env-create-4d5e6f // PATH entry подтверждён.
```

UI не рисует фиктивный успех: status берётся из `EnvironmentExecutionReport` после фактической verification.

Подробнее: [`docs/CYBER_CONTROL_CENTER.md`](docs/CYBER_CONTROL_CENTER.md).

## 📡 Control Node telemetry

Главный экран показывает:

- версию, host, OS и architecture;
- PID и uptime;
- portable/user-data mode;
- готовность Profile Engine, Data Core и Environment Provider;
- число User/Machine variables;
- число User/Machine PATH entries;
- количество rollback checkpoint;
- размер SQLite;
- live event feed;
- текущую threat/safety posture.

## 🗃️ Profile Vault

Profile Vault автоматически индексирует:

```text
<WINSTATE_HOME>/profiles
./samples/**/*.yaml
./samples/**/*.yml
```

При запуске из корня репозитория все sample-профили сразу появляются в меню. Анализ проходит полный pipeline:

```text
parse → includes/extends → variables → normalization → validation
```

## 🛡️ Environment Provider

Поддерживается реальное управление:

- User environment variables;
- Machine environment variables;
- User `PATH` entries;
- Machine `PATH` entries;
- созданием и изменением переменных;
- добавлением, удалением и перестановкой управляемых PATH entries;
- checkpoint, verification и rollback;
- SQLite transaction history.

Неописанные переменные и неизвестные элементы PATH не удаляются.

### User scope

```yaml
environment:
  user:
    DEV_MODE: "true"

  userPath:
    - path: "C:\\Dev\\bin"
      state: present
      position: append
```

### Machine scope

```yaml
environment:
  machine:
    COMPANY_MODE: "managed"
```

Machine actions получают risk level `Medium`, требуют отдельного подтверждения и elevated terminal.

## ⚙️ CLI

Интерактивный дизайн не заменяет automation-режим:

```powershell
winstate environment status
winstate environment plan .\samples\environment-provider\user-sandbox.yaml
winstate environment apply .\samples\environment-provider\user-sandbox.yaml --yes
winstate environment checkpoints
winstate environment rollback <manifest.json> --yes
```

Для Machine scope:

```powershell
winstate environment apply .\profile.yaml --yes --allow-machine
```

## 💾 Checkpoint и история

Перед первым изменением создаётся:

```text
<WINSTATE_HOME>/backups/environment/<transaction-id>/
├── manifest.json
├── env-create-....json
├── env-modify-....json
└── env-remove-....json
```

SQLite хранит:

- `Transactions`;
- `TransactionActions`;
- `ActionBackups`.

## ✅ Текущее состояние

| Возможность | Статус |
|---|---|
| NexRoute-inspired Cyber Control Center | ✅ |
| Boot/shutdown trace | ✅ |
| Номерные operation channels | ✅ |
| Animated action pipelines | ✅ |
| Live event feed и transaction stream | ✅ |
| Автоиндексация sample-профилей | ✅ |
| Полный YAML Profile Engine | ✅ |
| Environment discovery и diff | ✅ |
| Risk-aware execution plan | ✅ |
| User/Machine variables и PATH | ✅ |
| Checkpoint перед apply | ✅ |
| Post-apply verification | ✅ |
| Automatic/manual rollback | ✅ |
| SQLite transaction history | ✅ |
| Ubuntu + Windows CI | ✅ |
| Multi-provider Apply Engine | ⏭️ следующий этап |

## 🧪 Проверки

GitHub Actions выполняет на Ubuntu и Windows:

```text
restore → build with warnings-as-errors → unit tests
        → Profile Engine → Cyber Control Center demo
        → Environment status → Doctor → SQLite
```

На Windows дополнительно выполняется настоящий временный сценарий:

```text
plan → apply → checkpoint → verify → rollback
     → assert variable and PATH fully restored
```

Cyber UI smoke test запускается без клавиатуры:

```powershell
winstate ui --demo --home .\.ci-winstate
```

## 🧱 Архитектура

```text
CyberTerminalShell
        ↓
WinState.App workflows
        ↓
WinState.Core ───────────── Profile Engine / validation / planning
        ├── WinState.Providers.Environment
        ├── WinState.Infrastructure
        ├── WinState.Storage ───────── SQLite history / migrations
        └── WinState.Domain ────────── resources / actions / providers
```

Terminal frontend не содержит системной бизнес-логики и не может обойти safeguards.

## ⚠️ Ограничения alpha

- системный apply пока доступен только для environment-секции;
- secrets adapter ещё не реализован;
- нет общего resume/reboot engine;
- Machine scope зависит от реальных прав процесса;
- WinState не заменяет полный backup Windows.

## 🗺️ Следующий этап

`0.6.0-alpha.1` — общий **multi-provider Apply Engine**:

```text
multiple providers → unified transaction → dependency graph
                   → risk groups → resume/reboot → rollback
```

## 📦 Portable ZIP

```powershell
.\scripts\package.ps1
```

Архив и SHA-256 создаются в `artifacts/`.

## 📄 Лицензия

MIT License — см. [`LICENSE`](LICENSE).
