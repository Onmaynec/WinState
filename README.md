<p align="center">
  <img src="assets/banner.svg" alt="WinState — декларативное управление Windows" width="100%" />
</p>

<p align="center">
  <strong>Безопасное управление конфигурацией Windows как кодом.</strong>
</p>

<p align="center">
  <a href="docs/CAPTURE_DRIFT.md">📸 Снимки и отклонения</a> ·
  <a href="docs/SYSTEM_CONTROL.md">🪟 Системное управление</a> ·
  <a href="docs/PACKAGES_FEATURES.md">📦 Пакеты и компоненты</a> ·
  <a href="docs/APPLY_ENGINE.md">🧠 Движок применения</a> ·
  <a href="docs/AUTO_UPDATE.md">📡 Автообновление</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a>
</p>

---

## WinState `0.9.0`

Это стабильный релиз без суффикса `alpha`. Версия 0.9.0 добавляет создание проверяемых снимков текущей Windows-конфигурации и контроль отклонений без изменения системы.

```text
текущее состояние → capture → YAML + JSON-манифест + SHA-256
профиль → discovery → plan → drift report
```

## Быстрый старт

Требуется Windows 10/11 и .NET 8 SDK при запуске из исходников. Готовые релизные ZIP являются self-contained.

```powershell
git clone https://github.com/Onmaynec/WinState.git
cd WinState

dotnet restore
dotnet build -c Release
dotnet test -c Release

dotnet run --project src/WinState.Cli -- --help
```

## Создание снимка

```powershell
.\winstate.exe capture .\profiles\my-pc.yaml "Мой компьютер"
```

Capture экспортирует:

- User/Machine environment variables;
- User/Machine PATH;
- установленные пакеты WinGet;
- включённые Windows Optional Features.

Рядом создаётся файл `.snapshot.json` с SHA-256, временем создания, версией WinState и количеством ресурсов. Значения переменных с признаками паролей, токенов, ключей, credentials и строк подключения не записываются.

## Контроль отклонений

```powershell
.\winstate.exe drift .\profiles\my-pc.yaml .\reports\drift.json
```

Drift выполняет только чтение состояния и построение Unified Apply Plan. Никакие изменения не применяются.

| Код | Результат |
|---:|---|
| `0` | отклонений нет |
| `10` | отклонения обнаружены |
| `3` | профиль невалиден |
| `6` | провайдер недоступен |

Подробнее: [`docs/CAPTURE_DRIFT.md`](docs/CAPTURE_DRIFT.md).

## Провайдеры

```text
environment        → User/Machine variables и PATH
packages.winget    → install / upgrade / uninstall
windows.features   → DISM enable / disable
windows.system     → Registry / Services / Startup / Scheduled Tasks
```

Все изменения проходят единый конвейер:

```text
YAML → discovery → deterministic graph → risk/admin gates
     → checkpoints → apply → verification → rollback
```

## Основные команды

```powershell
.\winstate.exe --version
.\winstate.exe doctor
.\winstate.exe validate .\profiles\workstation.yaml
.\winstate.exe capture .\profiles\current.yaml "Текущий компьютер"
.\winstate.exe drift .\profiles\current.yaml .\reports\drift.json
.\winstate.exe environment plan .\profiles\workstation.yaml
.\winstate.exe environment apply .\profiles\workstation.yaml --yes
```

## Безопасность

- план всегда строится до применения;
- reversible checkpoints создаются до первой мутации;
- успешный результат фиксируется только после verification;
- Machine/admin actions требуют отдельного разрешения;
- package upgrade/uninstall требуют irreversible gate;
- DISM запускается с `/NoRestart`;
- Registry ограничен `HKCU\Software` и `HKLM\SOFTWARE`;
- Capture не экспортирует секретоподобные переменные;
- Drift никогда не применяет изменения;
- updater проверяет SHA-256 и release marker.

## CI и релизы

GitHub Actions проверяет Ubuntu и Windows:

```text
restore → build → tests → CLI/version → profile samples → Forge demo
        → Capture/Validate/Drift → Environment rollback → package smoke
```

Стабильный тег `v0.9.0` публикует обычный GitHub Release и отмечает его как **Latest**. Релиз содержит:

```text
WinState-0.9.0-win-x64.zip
WinState-0.9.0-win-arm64.zip
индивидуальные .sha256
SHA256SUMS
```

## Ограничения

- WinGet inventory не всегда сообщает scope установки, поэтому captured packages необходимо проверить перед apply;
- Capture экспортирует только включённые Optional Features;
- системные Registry/Service/Startup/Task resources захватываются только через явно управляемые профили;
- Authenticode signing запланирован для версии 1.0;
- WinState не заменяет резервное копирование Windows.

## Лицензия

MIT License — см. [`LICENSE`](LICENSE).
