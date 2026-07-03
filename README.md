# VoxelTycoon: duplicate mod-assembly loading — repro + fix PoC

**TL;DR (EN):** VoxelTycoon's `ModLoader.LoadAssembly` uses `Assembly.Load(File.ReadAllBytes(...))`,
creating a new unloadable in-memory assembly copy on every scan (main-menu scan + game load + every
new game/save in the same process). The bundled 0Harmony stores patch state serialized
(`Patch.patchMethod` is `[NonSerialized]`, kept as methodToken+moduleGUID) and re-resolves methods
into the **first** loaded copy whenever a second patcher touches the same method. Patch code then
executes in copy #1 while `Mod.Initialize` (statics, asset handlers → building component types) ran
on copy #2 — `is`/`IsAssignableFrom` checks silently fail. This repo contains a minimal two-mod
repro and a proof-of-concept runtime fix (assembly dedup by `AssemblyName.FullName`).

---

Три мини-мода к баг-репорту «DLL модов грузятся дублирующимися экземплярами; Harmony
перепривязывает патчи к устаревшей копии».

## Состав

- `modA/`, `modB/` — **репро**: два одинаковых мода, оба постфиксят один ванильный метод
  (`Currency.Format`, вызывается UI постоянно). Каждый постфикс логирует, видит ли он статик,
  выставленный его же `Initialize`, и хэш экземпляра своей сборки.
- `dedupfix/` — **PoC фикса**: `MainMenuMod`, ставящий Harmony-префикс на
  `ModLoader.LoadAssembly`, который вместо загрузки дубля возвращает уже загруженную сборку с тем
  же `AssemblyName.FullName` (+ самовосстановление после глобального `UnpatchAll()` в
  `ModManager.OnDeinitialize`).

## Сборка

1. Скопировать из `<игра>/VoxelTycoon_Data/Managed/` в `lib/`:
   `VoxelTycoon.dll`, `UnityEngine.CoreModule.dll`, `0Harmony.dll` (в репозиторий не коммитятся).
2. `dotnet build modA/src` / `modB/src` / `dedupfix/src`.
3. В `<игра>/Content/` создать по папке на мод, положить туда `pack/mod.json` + собранную DLL.

## Воспроизведение

1. Включить в главном меню **оба** репро-пака (A и B), фикс выключен.
2. Запустить любую игру, подождать пару секунд, посмотреть
   `%USERPROFILE%/AppData/LocalLow/VoxelTycoon/VoxelTycoon/Player.log`.

Ожидаемо (баг): один из модов (который пропатчил метод первым) пишет

```
[ReproX] patchAsm=#AAAAAAAA initSeen=False initAsm=#0 (BUG: patch executes in a duplicate assembly copy where Initialize never ran)
```

— его патч исполняется в копии сборки из меню-скана: `initSeen=False`, хотя `Initialize`
гарантированно отработал (строка `[ReproX] Initialize ran on assembly #BBBBBBBB` выше по логу,
хэш другой).

3. Включить дополнительно пак Assembly Dedup Fix, перезапустить игру, повторить: оба мода пишут
   `initSeen=True (OK)`, в логе видны строки `[VTDedupFix] Reusing already-loaded …`.

## Механизм

`ModLoader.LoadAssembly` = `Assembly.Load(File.ReadAllBytes(...))` — новый невыгружаемый экземпляр
сборки на каждый вызов; сканы идут минимум дважды за запуск (`MainMenuModLoader` в меню +
`GameModLoader` при старте игры) и по разу на каждый заход в сейв без перезапуска. Выгрузки нет и
быть не может (Unity Mono не умеет выгружать сборки), копии копятся до выхода из процесса.

0Harmony хранит патчи сериализованными: `HarmonySharedState.state : Dictionary<MethodBase, byte[]>`,
`Patch.patchMethod` — `[NonSerialized]`, хранится парой methodToken+moduleGUID и при перечитывании
резолвится через `AppDomain...GetLoadedModules().First(m => m.ModuleVersionId == moduleGUID)` — в
самую старую копию. Пока мод один на методе — его `PatchAll` работает с прямыми ссылками и всё
корректно; как только второй Harmony-инстанс (другой мод) патчит тот же метод, состояние
перечитывается и методы первого мода молча перепривязываются к инертной меню-копии, где
`Initialize` не выполнялся.

Исполняемый код байт-в-байт тот же, поэтому моды, работающие только с ванильными типами, ничего не
замечают. Ломаются моды, которые проверяют **собственные** типы в патчах (например, свои
компоненты-здания, созданные через кастомный `AssetHandler`): `is`/`IsAssignableFrom`/касты между
копиями дают false или `InvalidCastException`. Ни исключений, ни строк в логе.

## Предлагаемое исправление на стороне игры

Кэшировать загруженные сборки в `ModLoader` по ключу «путь к файлу + хэш содержимого» (или MVID) и
при повторных сканах возвращать закэшированный экземпляр — ровно то, что делает PoC, только без
Harmony-акробатики с самовосстановлением. Заодно уйдёт утечка памяти на каждом цикле меню→игра.

Куда смотреть: `ModLoader.LoadAssembly`, `MainMenuModLoader.Load`, `GameModLoader.Load`,
геттер `HarmonyLib.Patch.PatchMethod`.
