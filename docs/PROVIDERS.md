# 🔌 Контракты провайдеров

Провайдер — изолированный модуль, отвечающий за один класс ресурсов.

## Обязательный жизненный цикл

```text
DiscoverAsync → PlanAsync → ApplyAsync → VerifyAsync
```

Rollback-поддержка объявляется отдельным `IRollbackProvider`.

## Возможности

`ProviderCapabilities` сообщает, поддерживает ли модуль capture, apply, rollback, removal, offline mode, elevation и reboot.

## Запланированные провайдеры

| Этап | Провайдеры |
|---|---|
| Vertical slice | Environment |
| MVP | Packages/WinGet, Windows Features, Services, Registry, Git, PowerShell, Files |
| После MVP | Terminal, WSL, Network, Firewall, Scheduled Tasks |

Новый провайдер не должен требовать изменений доменного ядра.
