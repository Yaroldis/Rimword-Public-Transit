# RimWorld: Public Transit

**Status: experimental bootstrap / not playable yet** · **Target: RimWorld 1.6**

Мод для будущей автоматической сети общественного транспорта: фиксированные маршруты и остановки, самостоятельная посадка и высадка колонистов, пересадки, сохранение исходных заданий. Базовый транспорт планируется брать из [Vehicle Framework](https://github.com/SmashPhil/Vehicle-Framework).

### Уже есть

- `About.xml`: поддержка RimWorld **1.6**, зависимости Harmony и VF.
- Минимальная C#-сборка `.NET Framework 4.7.2` с сообщением в журнале игры.
- Размещаемая остановка-прототип в категории «Разное»; пока без посадки.
- Базовые `TransitRoute` и `TransitMapComponent` для сохранения маршрутов в игре.
- Проверка XML/ресурсов, GitHub Actions для CI и ZIP-релиза.

**Нет:** автономного движения, пассажирского ИИ, расписания, интерфейса линий. Это следующие этапы, а не готовая функция.

### Начать

[Пошаговая инструкция для Windows](docs/GETTING_STARTED.ru.md) · [Архитектура](docs/ARCHITECTURE.md)

```bash
python tools/validate_mod.py
dotnet build Source/PublicTransit/PublicTransit.csproj -c Release
python tools/package_mod.py --version dev
```

NuGet-пакет `Krafs.Rimworld.Ref 1.6.4871` предоставляет ссылочные сборки, поэтому **не нужно загружать игровые DLL в Git**. Для реальных игровых тестов RimWorld и Vehicle Framework должны быть установлены отдельно.

**Пакет мода:** `RimTransit.PublicTransit` (не менять после публичного релиза без миграции). Автор и лицензию согласуем перед публикацией.
