# 📝 Формат профиля

Основной формат — человекочитаемый YAML.

```yaml
schemaVersion: 1

metadata:
  name: Developer Workstation
  description: Рабочая среда разработчика
  profileVersion: 1

settings:
  strictMode: false
  removeUnmanagedPackages: false
  allowReboot: false

environment:
  user:
    DEV_MODE: "true"
  machine:
    COMPANY_TOOLS: "C:\\Tools"
```

## Правила безопасности

- секреты не записываются напрямую;
- неизвестные дополнительные ресурсы не удаляются по умолчанию;
- `strictMode` распространяется только на ресурсы с подтверждённым ownership;
- абсолютные пути из includes должны оставаться внутри профиля, если нет явного разрешения;
- циклические includes и extends запрещены.

## Текущая поддержка

Bootstrap-reader читает `schemaVersion`, `metadata.name`, `metadata.description`, `environment.user` и `environment.machine`. Полный YAML Engine, includes, наследование, variables и conditions входят в следующий этап.

JSON Schema находится в [`schemas/winstate.schema.json`](../schemas/winstate.schema.json).
