# Первый запуск (Windows, RimWorld 1.6)

1. Установи **Visual Studio Community** с компонентом **.NET desktop development** и поддержкой .NET Framework 4.7.2, либо .NET SDK 8 и 4.7.2 Developer Pack.
2. Распакуй проект. Открой `PublicTransit.sln` в Visual Studio.
3. Восстанови NuGet-зависимости (Visual Studio сделает это при сборке) и выбери `Release`.
4. Собери проект. DLL должна появиться в `1.6/Assemblies/PublicTransit.dll`.
5. Скопируй **всю папку `rimworld-public-transit`**, содержащую `About`, `Defs`, `Textures`, `Languages`, `1.6`, в папку `RimWorld/Mods/PublicTransit` (можно также использовать символьную ссылку/junction).
6. В списке модов включи Harmony, затем Vehicle Framework, затем Public Transit. Нужен также любой мод, добавляющий транспорт, только для будущих тестов с машинами.
7. Включи режим разработчика в RimWorld. После запуска в логе должно быть `[Public Transit] Bootstrap loaded for RimWorld 1.6.`.
8. Открой карту, вкладку **Разное (Misc)**; построй три «остановки общественного транспорта», создай линию через кнопку на первой остановке, добавь остальные и проверь сохранение/загрузку.

Остановки пока **не перевозят** колонистов: версия 0.2.0 реализует только редактор маршрутной сети. Подробный [план проверки](TEST_PLAN.ru.md).

## Быстрые проверки

```powershell
python tools/validate_mod.py
# Выполнять после установки .NET SDK и в каталоге репозитория:
dotnet build Source/PublicTransit/PublicTransit.csproj -c Release
# Собрать ZIP для установки в игру:
python tools/package_mod.py --version dev
```

## Публикация на GitHub (после создания пустого репозитория)

Создай **пустой** репозиторий `rimworld-public-transit` без README/.gitignore/license (они есть внутри архива). Затем в корне распакованного проекта:

```powershell
git remote add origin https://github.com/ВАШ_ЛОГИН/rimworld-public-transit.git
git push -u origin main
```

Далее для релиза: `git tag v0.2.0`, `git push origin v0.2.0`. Workflow создаёт **черновик** GitHub Release, а не публикует его сразу. Перед тегом обнови `modVersion` в `About/About.xml` и проверь мод в игре.

## Обратная связь

Если сборка или загрузка не работает, пришли полный текст первой ошибки (в Visual Studio или логе RimWorld), версию игры и используемую версию Vehicle Framework.
