# SsMod — заметки для агента

Мод для Space Station 14 (MusyaLoader/Marsey), C#, HarmonyLib.

## Пути

| Что | Путь |
|---|---|
| Этот мод | `/home/ntx/dev/auto/ex/mod-ss` |
| Соседние моды | `/home/ntx/dev/auto/ex/mcp-mod`, `/home/ntx/dev/auto/ex/tagmod` |
| **Исходники SS14 (контент)** | `/home/ntx/dev/ss14servs/space-stories-mc14` |
| **Исходники движка (RobustToolbox)** | `/home/ntx/dev/ss14servs/space-stories-mc14/RobustToolbox` |
| Исходники лаунчера (MusyaLoader) | `/home/ntx/dev/MusyaLoader` |
| Установка лаунчера | `/home/ntx/Рабочий стол/ss14` |
| **Папка модов лаунчера** | `/home/ntx/Рабочий стол/ss14/Marsey/Mods` |
| Папка модов (bin_x64, тоже копируется) | `/home/ntx/Рабочий стол/ss14/bin_x64/Marsey/Mods` |
| Второй лаунчер (если нужен) | `/home/ntx/app/g/ss12/Marsey/Mods` |
| **Логи клиента и лаунчера** | `~/.local/share/Space Station 14/launcher/logs/` (`client.stdout.log` — вывод игры вместе с `[SsMod]`, `client.marsey.log`, `launcher-YYYYMMDD.log`; перезаписываются на каждый запуск) |
| Движки, скачанные лаунчером | `~/.local/share/Space Station 14/launcher/engines/<версия>.zip` |
| Контент клиента (все форки) | `~/.local/share/Space Station 14/launcher/content.db` (SQLite) |

Компилируемые DLL игры, на которые ссылается `SsMod.csproj`, лежат в `Resources/`
этого репозитория — это копии `Content.Client.dll`, `Content.Shared.dll`,
`Robust.Client.dll`, `Robust.Shared.dll`, `Robust.Shared.Maths.dll`.

## Сборка и деплой

Системный `dotnet` **без SDK** (только рантаймы). SDK лежит отдельно:

```bash
export PATH="$HOME/.dotnet:$PATH"   # SDK 10.0.401
cd /home/ntx/dev/auto/ex/mod-ss
dotnet build -c Release            # -> bin/SsMod.dll
cp bin/SsMod.dll "/home/ntx/Рабочий стол/ss14/Marsey/Mods/SsMod.dll"
cp bin/SsMod.dll "/home/ntx/Рабочий стол/ss14/bin_x64/Marsey/Mods/SsMod.dll"
```

Копируется только `SsMod.dll` — лаунчеру нужен именно он.

## Как мод грузится

- `Entry.cs` → `ModEntry.Entry()` ждёт `Content.Client` и зовёт
  `new Harmony("local.ssmod").PatchAll(Assembly.GetExecutingAssembly())`.
  То есть **любой `[HarmonyPatch]`-класс в любом .cs этого репозитория применяется сам**.
- `Sedition.cs` — класс с точным именем `Sedition` в глобальном неймспейсе;
  MarseyLoader ищет его рефлексией и вещает на поля `hideDelegate` /
  `hideManifestDelegate`, чтобы скрыть сборку. Не переименовывать и не переносить.
- `SubverterPatch.cs` — аналогично, глобальный неймспейс, по имени ищется загрузчиком.
- `Marsey/Config/MarseyVars.cs` в лаунчере: папка модов = `Marsey/Mods` относительно
  каталога игры; `MarseyPatchFolder`.

## Факты по движку (проверено по исходникам, не по памяти)

- `Robust.Shared.Graphics.Eye` (`Robust.Shared/Graphics/Eye.cs`):
  `Zoom` и `Scale` — **virtual property с телом**, оба пишут в одно поле `_scale`
  (`Zoom = 1/_scale`). Дефолт `_scale = Vector2.One / 2f`, то есть `Zoom == 2`, не 1.
- `Robust.Client.Graphics.FixedEye : Eye` — sealed, ничего не переопределяет.
- Весь игровой код меняет зум только через `Eye.Zoom = ...`:
  `Content.Client/Commands/ZoomCommand.cs`, хоткеи,
  `Robust.Shared/GameObjects/Systems/SharedEyeSystem.cs` (`SetZoom`, `UpdateEye`),
  `Content.Client/Eye/EyeLerpingSystem.cs`, `Content.Shared/Movement/Systems/SharedContentEyeSystem.cs`.
  Значит, перекрытия сеттеров `Zoom` и `Scale` — замкнутый контур.
- `IEyeManager` — `Robust.Client.Graphics`, `CurrentEye` типа `IEye`.

## Как в моде сделана блокировка радиуса обзора

`fnc/ViewRadius.cs` — по образцу `SsWrite`/`LightEnabledGuard` из `fnc/Fullbright.cs`:

- `ViewRadius.Value` — значение, публично только на чтение;
- `ViewRadius.Set(Vector2)` — **единственная** функция, которая пишет в камеру;
  она же клампит в `Min..Max`;
- `EyeZoomGuard` (`Eye.set_Zoom`) и `EyeScaleGuard` (`Eye.set_Scale`) — `Prefix`
  возвращает `false`, если пишет не мод, то есть чужие записи отсекаются;
- блокировка взводится один раз на PostInit: `ViewRadius.Lock()`;
- `fnc/Zoom.cs` (ctrl+колесо) вызывает `ViewRadius.Step(factor)` — больше нигде
  в моде нет прямых записей в `eye.Zoom`.

## Версии: 276, 277, 290 — проверять все

У пользователя **три** разных клиента, и сборку надо гонять против всех:

| Клиент | Движок | Где брать DLL для проверки |
|---|---|---|
| Свой сервер (space-stories) | **276** | `Resources/` этого репозитория |
| Форк `fish_station_stable` (perseus) | **277** | движок — `launcher/engines/277.0.4.zip`, контент — из `content.db` |
| Форк `frontier` | **290** | `/tmp/opencode/g290` |

Достать DLL 277-го из `content.db` (SQLite, контент не лежит файлами на диске):

```bash
sqlite3 ~/.local/share/Space\ Station\ 14/launcher/content.db \
  "select Id, ForkId, datetime(LastUsed,'localtime') from ContentVersion order by LastUsed desc limit 3;"
# дальше: ContentManifest(VersionId, Path) -> ContentId -> Content(Compression, Size, Data)
# пути вида 'Assemblies/Content.Client.dll'; Compression 0 — как есть, 1 — zlib, 2 — zstd
```

Проверка совместимости — только `jitcheck` (принудительный JIT каждого метода) по
всем трём наборам; он ловит `MissingMethodException`/`TypeLoadException`, которых
на своей версии не видно:

```bash
cd /tmp/opencode/jitcheck && dotnet run -c Release -- <mod.dll> /tmp/opencode/fish277
```

### Известные расхождения 276 ↔ 277 ↔ 290

- **Растворы.** 276/290: `SolutionContainerManagerComponent` (`HashSet<string> Containers`)
  + сущность-раствор в слоте `solution@<имя>` со своим `SolutionComponent`.
  277: тип переименован в `SolutionManagerComponent`, а растворы лежат ссылками —
  `Dictionary<string, Entity<SolutionComponent>> Solutions`. Прямая ссылка на любой
  из этих типов/методов = `MissingMemberException` на чужой версии, поэтому
  `fnc/ReagentExamine.cs` ищет компонент по имени (`Compat.FindType`) и читает словарь
  рефлексией, а слоты — через нейтральный `ContainerManagerComponent.Containers`.
- `Entity<T>.Comp`, `SolutionComponent.Solution`, `Solution.Contents`,
  `ContainerManagerComponent.Containers` — публичные **поля**, одинаковые во всех трёх.
- `ExamineSystem.UpdateTooltipInfo` есть везде, но число параметров разное
  (276 — 5, 290 — 4), поэтому патч по имени и Prefix только с общими параметрами.

## Правила

- **Делать только то, что попросили напрямую.** Жалоба или вопрос («нахуя мне это»,
  «почему так») — не команда. Ничего не удалять, не добавлять и не «чинить»
  без явной просьбы вроде «убери», «сделай», «поменяй». Если непонятно — спросить.
- Не хардкодить внутренние имена типов движка без проверки по исходникам RobustToolbox:
  падение `TargetMethod` роняет весь `PatchAll`.
- Логи пишем через `Console.WriteLine("[SsMod] ...")` — так уже сделано в проекте.
- Комментарии и сообщения в проекте — на русском.
- Файлы правим только инструментами `edit` / `write`. Никаких `python3`,
  `sed`, `perl` и heredoc-скриптов для редактирования кода.