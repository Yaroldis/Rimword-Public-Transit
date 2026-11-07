# RimWorld: Public Transit

<<<<<<< ours
<<<<<<< ours
<<<<<<< ours
<<<<<<< ours
<<<<<<< ours
**Status: experimental bootstrap / not playable yet** · **Target: RimWorld 1.6**
 HEAD
Мод для будущей автоматической сети общественного транспорта: фиксированные маршруты и остановки, самостоятельная посадка и высадка колонистов, пересадки, сохранение исходных заданий.

 f978e3f (feat: add transit network editor)
=======
**Status: 0.2.0-dev / route editor only** · **Target: RimWorld 1.6**
=======
**Status: 0.3.0-dev / vehicle assignment** · **Target: RimWorld 1.6**
>>>>>>> theirs
=======
**Status: 0.3.1-dev / vehicle assignment safety** · **Target: RimWorld 1.6**
>>>>>>> theirs
=======
**Status: 0.4.0-dev / vehicle assignment safety** · **Target: RimWorld 1.6**
>>>>>>> theirs
=======
**Status: 0.4.1-dev / vehicle assignment safety** · **Target: RimWorld 1.6**
>>>>>>> theirs

Мод для будущей автоматической сети общественного транспорта. На текущем этапе реализованы **остановки, линии и назначение транспорта**. Пока без движения машин, пассажирского ИИ и расписаний. Ведущая техника берётся из модов, основанных на [Vehicle Framework](https://github.com/SmashPhil/Vehicle-Framework).

## Готово к тестированию

- Размещаемая остановка в категории «Разное», возможность задать пользовательское название.
- Автоматическая регистрация остановок при постройке; исключение из всех линий при удалении постройки.
- Кнопки остановки: «Создать линию», «Назвать остановку», «Транспортная сеть».
- Окно линий: добавление остановок в нужном порядке, перемещение ↑/↓, удаление из линии, переименование и удаление линии.
- Назначение/смена/снятие **одной конкретной** машины Vehicle Framework на линию. Поддерживаются транспортные моды с наследниками `Vehicles.VehiclePawn` (для теста: Vanilla Vehicles Expanded, Trains of the Rim).
- Выбор только собственного ведущего транспорта на текущей карте; нельзя назначить одну машину на две линии. Вагоны Trains of the Rim исключаются, а локомотивы остаются доступными (определение по игровым Def/текстурам).
- Если любой компонент VF теряет не меньше выбранной доли прочности (по умолчанию 50%), машина автоматически освобождает линию и остаётся доступной для ремонта. Порог изменяется в настройках Public Transit. Проверка — каждые 250 игровых тиков.
- Ссылки уничтоженных машин очищаются, у транспорта вне карты назначение сохраняется. Если состояние компонентов недоступно, мод выдаёт предупреждение и не снимает машину на основании предположений.
- Сохранение и загрузка назначенного транспорта через ссылку на игровой объект (`Scribe_References`).
- Сохранение названий остановок и линий через Scribe (`MapComponent`, `IExposable`, ссылки на игровые постройки).
- Русский и английский интерфейс. Нет Harmony-патчей и прямых вызовов экспериментального API VF. Для обнаружения автомобилей используется проверка наследования runtime-типа.
- GitHub Actions: проверки XML, сборка под `net472`, установочный CI-артефакт с одной вложенностью, draft релизы по тегу.

**Не реализовано:** движение транспорта, проверка пассажирской вместимости, управление связанными составами и рельсами, пассажиры, расписания, пересадки, навигационный граф. Не воспринимать `0.3.0-dev` как готовый транспортный мод.

## Сборка

[Настройка Windows](docs/GETTING_STARTED.ru.md) · [Архитектура](docs/ARCHITECTURE.md) · [Тест-план 0.3.0](docs/TEST_PLAN.ru.md)

```bash
python tools/validate_mod.py
dotnet build Source/PublicTransit/PublicTransit.csproj -c Release
python tools/package_mod.py --version dev
```

Сборка в CI не требует копировать игровые DLL в репозиторий: используется `Krafs.Rimworld.Ref 1.6.4871`. Для игровых тестов требуется RimWorld 1.6, Harmony и VF.

**Пакет мода:** `RimTransit.PublicTransit` (не менять без миграции). Автор и лицензию согласуем перед публикацией.
<<<<<<< ours
>>>>>>> theirs
=======

## Experimental road vehicle loop (0.4.1-dev)

Once a vehicle is assigned to a line with at least two stops, manually board a
conscious player-controlled pawn into its VF driver role and select **Start line**
from the Transit network UI. The controller targets free cells near each stop,
waits briefly at arrival, then advances to the next stop. **Stop line** immediately
requests a VF halt.

This requires a compatible VF 1.6 vehicle path follower. If movement / safe stop
methods do not exist in the installed VF build, the line refuses to start rather
than bypass safety checks. Use a clear, wide road and a small VVE vehicle first.
Passenger AI and `JobDriver_TransitDriver` are not implemented yet. Locomotives
can be assigned but automated train travel is intentionally disabled until we
implement coupled consist and rails support. The nearest suitable docking cell
heuristic is temporary and may require road adjustments.
<<<<<<< ours
>>>>>>> theirs
=======

### 0.4.1: Why the vehicle sometimes stopped early

The first movement prototype selected a cell that a **pawn** could stand on and measured arrival from the vehicle's origin. This fails for multi-cell vehicles. The controller now proposes anchors whose **whole** footprint can fit near the stop, and only declares arrival when the actual vehicle body is adjacent. Vehicle Framework remains responsible for final pathfinding.

If the engine is off, Public Transit tries to switch on the VF ignition controller **only when a living driver occupies the driving role**. If the installed VF build does not expose a compatible setter, the line waits for manual ignition instead of falsely reporting a road obstruction. The engine stays on when stopping the service; the player can switch it off in VF.
>>>>>>> theirs
