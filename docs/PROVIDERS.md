# 🔌 Контракты провайдеров

Провайдер — изолированный модуль, отвечающий за один класс ресурсов. UI, CLI и storage не должны содержать его системную реализацию.

## Обязательный жизненный цикл

```text
DiscoverAsync → PlanAsync → ApplyAsync → VerifyAsync
```

Rollback-поддержка объявляется отдельным `IRollbackProvider`:

```text
PrepareRollbackAsync → ApplyAsync → VerifyAsync
                                  ↘ RollbackAsync on failure/request
```

## Возможности

`ProviderCapabilities` сообщает, поддерживает ли модуль:

- capture/discovery;
- apply;
- rollback;
- removal;
- offline mode;
- elevation;
- reboot.

Каждое действие обязано содержать identity, operation, risk, explanation, dependencies, elevation/reboot flags и признак rollback support.

## ✅ Environment Provider

Версия `0.4.0-alpha.1` содержит первый production-shaped provider:

| Возможность | Статус |
|---|---|
| User/Machine variable discovery | ✅ |
| PATH discovery | ✅ |
| Deterministic plan | ✅ |
| Variable create/modify | ✅ |
| PATH add/remove/reorder | ✅ |
| User/Machine risk policy | ✅ |
| Checkpoint | ✅ |
| Apply | ✅ |
| Verification | ✅ |
| Rollback | ✅ |
| SQLite history | ✅ |
| Windows CI vertical slice | ✅ |

Реализация находится в `src/WinState.Providers.Environment`. Системный доступ изолирован интерфейсом `IEnvironmentStore`; unit-тесты используют `InMemoryEnvironmentStore`.

Подробности: [Environment Provider](ENVIRONMENT_PROVIDER.md).

## Правила нового provider

Новый provider должен:

1. нормализовать resource identity;
2. возвращать только фактическое текущее состояние;
3. строить детерминированный план без побочных эффектов;
4. объяснять каждое изменение;
5. честно указывать risk, elevation и reboot;
6. создавать checkpoint до изменения, если заявлен rollback;
7. проверять результат повторным чтением системы;
8. не удалять unmanaged resources;
9. не записывать секреты в plan/history/logs;
10. иметь in-memory/fake adapter для unit-тестов;
11. иметь platform smoke test там, где это безопасно.

Новый provider не должен требовать изменения доменного ядра для обычных ресурсов и действий.

## Roadmap провайдеров

| Этап | Провайдеры |
|---|---|
| ✅ Vertical slice | Environment |
| Apply Engine | общая cross-provider orchestration |
| MVP | Packages/WinGet, Windows Features, Services, Registry, Git, PowerShell, Files |
| После MVP | Terminal, WSL, Network, Firewall, Scheduled Tasks |
