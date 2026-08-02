# 📡 Автоматическое обновление и Recovery WinState

## Обзор

`WinState.Update` проверяет GitHub Releases, сравнивает semantic versions и безопасно подготавливает официальный Windows package к установке. WinState 1.0 также умеет восстановить установку из updater backup.

## Режимы

Переменная `WINSTATE_AUTO_UPDATE`:

| Значение | Поведение |
|---|---|
| `off` | не проверять Releases |
| `check` | проверить и только сообщить |
| `prompt` | спросить перед скачиванием; значение по умолчанию |
| `install` | скачать, проверить и запланировать установку |

`WINSTATE_UPDATE_CHANNEL` принимает `stable` или `prerelease`. Для ветки 1.x рекомендуется `stable`.

## Проверка package

Для текущего runtime ожидаются ZIP и отдельный `.sha256`:

```text
WinState-<version>-win-x64.zip
WinState-<version>-win-x64.zip.sha256
```

или аналогичные файлы `win-arm64`.

Порядок проверки:

1. ZIP и checksum скачиваются отдельно.
2. Формат checksum проверяется как 64 hex-символа.
3. Для архива вычисляется SHA-256.
4. Несовпадение блокирует установку.
5. ZIP распаковывается с защитой от path traversal.
6. Проверяется `winstate.release.json`.
7. Product, version и runtime должны совпадать.

Release marker 1.0 содержит `authenticodeSigned`, который отражает фактический статус подписи package. Неподписанный package не выдаётся за подписанный.

## Установка

Работающие `winstate.exe` и DLL могут быть заняты Windows. Поэтому основной процесс:

1. загружает и проверяет package;
2. распаковывает его в staging;
3. создаёт updater script;
4. запускает отдельный PowerShell process;
5. завершает текущую session.

Updater ждёт завершения PID, сохраняет backup текущей установки, копирует release payload, пишет `update-success.txt` и перезапускает `winstate.exe`.

## Восстановление updater backup

Подготовка recovery без запуска:

```powershell
.\winstate.exe update prepare-restore <backup-directory> --install <directory>
```

Запуск восстановления:

```powershell
.\winstate.exe update restore <backup-directory> --yes
```

Можно явно указать каталог установки:

```powershell
.\winstate.exe update restore <backup-directory> --yes --install C:\Tools\WinState
```

Перед restore WinState:

1. проверяет наличие `winstate.exe` и `winstate.release.json` в backup;
2. создаёт safety backup текущей установки;
3. исключает пользовательские `.winstate`, `profiles` и `logs`;
4. создаёт `restore-update.ps1`;
5. после подтверждения запускает его отдельным процессом;
6. ждёт завершения текущего PID;
7. восстанавливает release payload;
8. пишет `restore-success.txt` и перезапускает приложение.

Ошибка сохраняется рядом со script в `restore-error.log`.

## Сохраняемые пользовательские данные

Updater и restore не используют пользовательские state directories как release payload:

```text
.winstate
profiles
logs
```

Рекомендуется хранить `WINSTATE_HOME` вне install directory.

## Source mode

Self-install определяется наличием одновременно:

```text
<AppContext.BaseDirectory>/winstate.exe
<AppContext.BaseDirectory>/winstate.release.json
```

При `dotnet run` updater может проверить новую версию, но не перезаписывает source checkout.

## Stable release pipeline

```text
restore → build → tests
→ optional Authenticode sign/verify
→ win-x64 package → win-arm64 package
→ release marker verification
→ ZIP + SHA-256 + SHA256SUMS
→ GitHub Release
```

Authenticode stage выполняется только при наличии официального signing certificate в защищённой CI-конфигурации. После packaging сертификат удаляется из runner store. SHA-256 и marker verification выполняются всегда.

Тег без prerelease-суффикса публикуется как stable и назначается **Latest**.

## Ограничения

- self-install и автоматический restore работают только в Windows;
- proxy/firewall может блокировать GitHub API или downloads;
- SHA-256 не заменяет доверенную Authenticode identity;
- delta updates не поддерживаются: загружается полный ZIP.
