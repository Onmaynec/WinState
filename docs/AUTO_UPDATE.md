# 📡 Автоматическое обновление WinState

## Обзор

`WinState.Update` проверяет актуальные GitHub Releases, сравнивает semantic version и безопасно подготавливает официальный Windows package к установке.

Update Uplink доступен в Nexus Control Fabric:

```text
[03] UPDATE UPLINK
```

При обычном интерактивном запуске выполняется фоновая проверка с локальным интервалом. Demo mode и неинтерактивные CLI-команды не запускают сетевую установку.

## Режимы

Переменная `WINSTATE_AUTO_UPDATE`:

| Значение | Поведение |
|---|---|
| `off` | не проверять Releases |
| `check` | проверить и только сообщить |
| `prompt` | проверить и спросить перед скачиванием; значение по умолчанию |
| `install` | скачать, проверить и запланировать установку без дополнительного вопроса |

Полностью автоматическая замена файлов всё равно возможна только в официальной release-сборке.

## Каналы

`WINSTATE_UPDATE_CHANNEL`:

- `stable` — игнорировать prerelease;
- `prerelease` — выбирать новейшую stable или prerelease версию.

Alpha-ветка WinState использует `prerelease` по умолчанию.

## Semantic Version

Поддерживаются версии вида:

```text
0.6.0
0.6.0-alpha.1
0.6.0-rc.2
v0.6.0-alpha.1
```

Comparison соответствует базовым SemVer-правилам:

- release новее prerelease той же core version;
- числовые prerelease identifiers сравниваются численно;
- `alpha.10` новее `alpha.2`;
- build metadata не влияет на порядок.

## Поиск release assets

Для текущего runtime ожидаются:

```text
WinState-<version>-win-x64.zip
WinState-<version>-win-x64.zip.sha256
```

или:

```text
WinState-<version>-win-arm64.zip
WinState-<version>-win-arm64.zip.sha256
```

Runtime определяется по architecture процесса и может быть переопределён через `WINSTATE_UPDATE_RUNTIME`.

## Проверка целостности

1. ZIP и checksum скачиваются отдельно.
2. Формат checksum проверяется: ровно 64 hex-символа.
3. Для ZIP вычисляется SHA-256.
4. Несовпадение удаляет загруженный архив и блокирует установку.
5. ZIP распаковывается только после успешной проверки.

SHA-256 защищает от повреждения и подмены одного asset относительно опубликованного checksum. Code signing будет добавлен отдельным release-этапом.

## Safe extraction

Каждый ZIP entry преобразуется в абсолютный путь и должен оставаться внутри staging directory. Entries с `../` или другим выходом за пределы каталога отклоняются.

После распаковки обязателен marker:

```text
winstate.release.json
```

Marker создаётся только release script и содержит product, version, runtime, repository и package timestamp.

## Почему нужен отдельный updater process

Работающий `winstate.exe` и загруженные DLL могут быть заняты Windows. Поэтому основной процесс:

1. готовит verified staging payload;
2. создаёт временный updater script;
3. запускает скрытый `powershell.exe`;
4. завершает текущую WinState session.

Updater:

1. ждёт завершения текущего PID;
2. делает backup существующей установки во временный каталог;
3. копирует staged release files;
4. пишет `update-success.txt`;
5. перезапускает `winstate.exe`.

Ошибка записывается в:

```text
%TEMP%\WinState\update-error.log
```

## Сохраняемые пользовательские данные

Updater не использует пользовательские state directories как источник release files. При обновлении сохраняются внешние/локальные данные, включая:

```text
.winstate
profiles
logs
```

Рекомендуется хранить `WINSTATE_HOME` вне install directory либо использовать стандартный user-data mode.

## Source mode

Self-install определяется наличием одновременно:

```text
<AppContext.BaseDirectory>/winstate.exe
<AppContext.BaseDirectory>/winstate.release.json
```

При `dotnet run` marker отсутствует. Update Uplink может проверить новую версию, но не перезаписывает source tree и показывает команду:

```powershell
git pull
```

## Конфигурация

| Переменная | Default | Назначение |
|---|---:|---|
| `WINSTATE_AUTO_UPDATE` | `prompt` | режим проверки/установки |
| `WINSTATE_UPDATE_CHANNEL` | `prerelease` | release channel |
| `WINSTATE_UPDATE_INTERVAL_HOURS` | `6` | минимальный интервал проверки |
| `WINSTATE_UPDATE_TIMEOUT_SECONDS` | `6` | timeout GitHub/download request |
| `WINSTATE_UPDATE_RUNTIME` | auto | target package runtime |
| `WINSTATE_UPDATE_REPOSITORY` | `Onmaynec/WinState` | источник Releases |

Пример stable-only:

```powershell
$env:WINSTATE_UPDATE_CHANNEL = "stable"
$env:WINSTATE_AUTO_UPDATE = "prompt"
.\winstate.exe
```

## Check ledger

Последний результат проверки хранится в:

```text
<WINSTATE_HOME>/updates/check-state.json
```

Ledger содержит время проверки, текущую версию, latest version и признак доступного обновления. Повреждённый ledger безопасно приводит к новой проверке.

## Release pipeline

Tag `v*` запускает `.github/workflows/release.yml`:

```text
restore → tests
→ win-x64 self-contained package
→ win-arm64 self-contained package
→ ZIP + SHA-256
→ GitHub Release assets
```

Tag с `-alpha`, `-beta` или `-rc` публикуется как prerelease.

## Ограничения

- self-install работает только в Windows;
- updater зависит от Windows PowerShell;
- proxy/firewall может блокировать GitHub API или asset downloads;
- checksum не заменяет Authenticode signing;
- откат версии приложения пока выполняется вручную из updater backup;
- delta updates не поддерживаются: загружается полный ZIP.
