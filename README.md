<p align="center">
</p>

<p align="center">
  <strong>Безопасное управление конфигурацией Windows и рабочим окружением как кодом.</strong>
</p>

<p align="center">
  <a href="docs/WORKSPACE_CONTROL.md">🧰 Workspace Control</a> ·
  <a href="docs/CAPTURE_DRIFT.md">📸 Снимки и отклонения</a> ·
  <a href="docs/SYSTEM_CONTROL.md">🪟 Системное управление</a> ·
  <a href="docs/PACKAGES_FEATURES.md">📦 Пакеты и компоненты</a> ·
  <a href="docs/APPLY_ENGINE.md">🧠 Движок применения</a> ·
  <a href="docs/AUTO_UPDATE.md">📡 Обновление и recovery</a> ·
  <a href="docs/MIGRATION_POLICY.md">🔄 Совместимость</a> ·
  <a href="docs/SECURITY.md">🛡️ Безопасность</a>
</p>

---

## WinState `1.0.0`

Первый major stable release добавляет Workspace Control: Git configuration, PowerShell modules, управляемые файлы и каталоги, постоянный ownership ledger, JSON/Markdown reports и rollback. Также добавлено безопасное восстановление установки из updater backup.

```text
workspace.json → validate → plan → ownership gates
               → checkpoint → apply → report → rollback
```

## Быстрый старт

Требуется Windows 10/11 и .NET 8 SDK при запуске из исходников. Релизные Windows ZIP являются self-contained.

```powershell
git clone https://github.com/Onmaynec/WinState.git
cd WinState

dotnet restore
dotnet build -c Release
dotnet test -c Release

dotnet run --project src/WinState.Cli -- --help
```

## Workspace Control

Безопасный пример находится в [`samples/workspace-control/developer-workspace.json`](samples/workspace-control/developer-workspace.json).

```powershell
.\winstate.exe workspace validate .\workspace.json
.\winstate.exe workspace plan .\workspace.json --report .\reports
.\winstate.exe workspace apply .\workspace.json --yes --report .\reports
.\winstate.exe workspace status
```

Manifest может описывать:

- глобальные Git settings;
- PowerShell modules в scope `CurrentUser`;
- UTF-8 files из `content` или `source`;
- управляемые каталоги;
- состояния `present` и `absent`.

Установка modules и удаления имеют независимые gates:

```powershell
.\winstate.exe workspace apply .\workspace.json --yes --allow-modules
.\winstate.exe workspace apply .\workspace.json --yes --allow-delete
```

WinState удаляет только ресурсы из собственного ownership ledger. Чужие файлы, каталоги и Git settings блокируются на стадии plan. Непустые каталоги и PowerShell modules автоматически не удаляются.

Подробнее: [`docs/WORKSPACE_CONTROL.md`](docs/WORKSPACE_CONTROL.md).

## Откат Workspace

Transaction manifest сохраняется после каждого действия:

```text
<WINSTATE_HOME>/backups/workspace/<transaction-id>/transaction.json
```

```powershell
.\winstate.exe workspace rollback .\transaction.json --yes
```

При ошибке apply обратимые действия автоматически откатываются в обратном порядке. Для заменяемых файлов сохраняются backups, а Git config возвращается к исходному значению.

## JSON и Markdown reports

`workspace plan` и `workspace apply` создают два отчёта:

```text
<WINSTATE_HOME>/reports/workspace/*.json
<WINSTATE_HOME>/reports/workspace/*.md
```

JSON предназначен для CI и автоматизации, Markdown — для ревью человеком. Каталог можно переопределить через `--report`.

## Восстановление updater backup

```powershell
.\winstate.exe update restore <backup-directory> --yes
```

Перед восстановлением создаётся safety backup текущей установки. Пользовательские каталоги `.winstate`, `profiles` и `logs` не перезаписываются release payload.

Для безопасной проверки без запуска restore:

```powershell
.\winstate.exe update prepare-restore <backup-directory> --install <directory>
```

## Создание снимка и Drift

```powershell
.\winstate.exe capture .\profiles\my-pc.yaml "Мой компьютер"
.\winstate.exe drift .\profiles\my-pc.yaml .\reports\drift.json
```

Capture экспортирует environment, PATH, надёжно распознанные WinGet packages и включённые Optional Features. Секретоподобные переменные исключаются. Drift выполняет только discovery и plan.

| Код | Результат |
|---:|---|
| `0` | отклонений нет |
| `10` | отклонения обнаружены |
| `3` | профиль невалиден |
| `6` | провайдер недоступен |

Подробнее: [`docs/CAPTURE_DRIFT.md`](docs/CAPTURE_DRIFT.md).

## Провайдеры

```text
environment          → User/Machine variables и PATH
packages.winget      → install / upgrade / uninstall
windows.features     → DISM enable / disable
windows.system       → Registry / Services / Startup / Scheduled Tasks
git.config           → global Git configuration
powershell.modules   → CurrentUser Install-Module
files.managed        → files и directories с ownership/backup
```

## Основные команды

```powershell
.\winstate.exe --version
.\winstate.exe doctor
.\winstate.exe validate .\profiles\workstation.yaml
.\winstate.exe capture .\profiles\current.yaml "Текущий компьютер"
.\winstate.exe drift .\profiles\current.yaml .\reports\drift.json
.\winstate.exe workspace plan .\workspace.json
.\winstate.exe workspace apply .\workspace.json --yes
.\winstate.exe environment plan .\profiles\workstation.yaml
.\winstate.exe environment apply .\profiles\workstation.yaml --yes
```

## Безопасность

- plan всегда строится до применения;
- apply/rollback требуют явного подтверждения;
- ownership запрещает удаление чужих ресурсов;
- module install и deletion имеют отдельные gates;
- backups создаются до замены или удаления файлов;
- state files и reports записываются атомарно;
- более новая неизвестная persisted schema блокирует mutation;
- Machine/admin actions требуют отдельного разрешения;
- DISM запускается с `/NoRestart`;
- Registry ограничен `HKCU\Software` и `HKLM\SOFTWARE`;
- Capture не экспортирует секретоподобные переменные;
- Drift не применяет изменения;
- updater проверяет SHA-256 и release marker.

## Совместимость

Версия 1.0 фиксирует правила миграции ownership, transaction manifests, SQLite и release marker. Downgrade persisted schema автоматически не выполняется.

Подробнее: [`docs/MIGRATION_POLICY.md`](docs/MIGRATION_POLICY.md).

## CI и релизы

GitHub Actions проверяет Ubuntu и Windows:

```text
restore → build → tests → CLI/version → profile samples → Forge demo
        → Workspace validate/plan/apply/idempotency/rollback
        → updater recovery → Capture/Drift → Environment rollback
        → self-contained package smoke
```

Stable tag `v1.0.0` публикует обычный GitHub Release и назначает его **Latest**:

```text
WinState-1.0.0-win-x64.zip
WinState-1.0.0-win-arm64.zip
индивидуальные .sha256
SHA256SUMS
```

Release pipeline поддерживает Authenticode через защищённые signing secrets. Поле `authenticodeSigned` в `winstate.release.json` честно отражает фактический статус подписи каждого package.

## Ограничения

- PowerShell modules устанавливаются из настроенного repository и автоматически не удаляются;
- Workspace files поддерживают UTF-8;
- ownership не присваивается существующим ресурсам по предположению — только после успешного apply;
- WinGet inventory не всегда сообщает scope установки;
- WinState не заменяет полноценное резервное копирование Windows.

## Лицензия

MIT License — см. [`LICENSE`](LICENSE).
