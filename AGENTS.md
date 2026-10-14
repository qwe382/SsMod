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
| Логи лаунчера/Marsey | `client.marsey.log` рядом с игрой (папка `Marsey/`) |

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

## Правила

- Не хардкодить внутренние имена типов движка без проверки по исходникам RobustToolbox:
  падение `TargetMethod` роняет весь `PatchAll`.
- Логи пишем через `Console.WriteLine("[SsMod] ...")` — так уже сделано в проекте.
- Комментарии и сообщения в проекте — на русском.