# Plan: Row Sync + Stock Minimization (Path A)

**Date:** 2026-04-09
**Mode:** Fast
**Branch:** N/A (fast mode)

## Settings

- **Testing:** Yes — тесты для CreateSyncedRows и DxfHeadlessRunner
- **Logging:** Verbose — детальные DEBUG-логи для Row Sync
- **Docs:** No — предупреждение warn-only

---

## Контекст и анализ проблемы

### Почему высоты рядов расходятся на 4 фасадах?

Архитектурная цепочка:

```
DxfHeadlessRunner (Program.cs)
    для каждого фасада:
        Preprocessor.DivideIntoRows(... syncedRowHeights: null)  ← БАГ: null!
            └── RowHeightForecaster.Forecast(... stock: <текущий склад>)
                    └── CP-SAT оптимизация: reward за использование остатков
                            ↑
                            Фасад 2 видит горы остатков от Фасада 1
                            и перекраивает сетку рядов под них (B2 feature)
```

Итог: каждый фасад получает **уникальную** сетку, потому что `syncedRowHeights` передаётся как `null`
вместо высот рядов Фасада 1.

### Инфраструктура уже готова — надо просто подключить

| Что уже есть | Файл |
|---|---|
| `FacadeInput.SyncedRowHeights` | `OrToolsModels.cs:198` |
| `Preprocessor.DivideIntoRows(... syncedRowHeights)` | `Preprocessor.cs:165` |
| `CreateSyncedRows()` — создаёт ряды по синхронизированным высотам | `Preprocessor.cs:87` |
| `Preprocessor.ConvertToFacadeInput(... syncedRowHeights)` | `Preprocessor.cs:25` |

**Не подключено нигде:** `DxfHeadlessRunner` никогда не передаёт `syncedRowHeights`.

### Критический баг в CreateSyncedRows

```csharp
// Preprocessor.cs:99 — ЭТО НЕПРАВИЛЬНО
syncedHeights = syncedHeights.OrderByDescending(h => h).ToList();
```

Порядок рядов от нижнего к верхнему **уничтожается** сортировкой по убыванию.
Ряды должны идти в том же порядке, что и на Фасаде 1 (снизу вверх).

---

## Анализ минимизации склада (Path A)

При синхронизации рядов (B2 отключён для Фасадов 2-4) склад накапливается из-за:

1. **Горизонтальные обрезки из оконных зон** — высота совпадает с рядами (уже зафиксирована),
   ширина используется PatternGenerator (B1-режим). Это работает.

2. **Bottleneck: MaxStockForSolver = 80** — при складе 200+ штук на Фасадах 3-4 солвер видит
   только 80 остатков. Остальные 120+ игнорируются. Текущий adaptive cap (150 макс) помогает,
   но недостаточен при накоплении > 300 остатков.

3. **Pass 2 threshold слишком жёсткий** — агрессивный проход сравнивает результаты и берёт
   лучший. При большом складе нужно явно приоритизировать утилизацию.

4. **"Мёртвый" склад** — узкие обрезки < 200 мм ширины, которые не влезают ни в один сегмент.
   Нет диагностики сколько их и почему они не используются.

### Где наибольший потенциал для экономии

| Точка | Потенциал | Сложность |
|---|---|---|
| Fix `CreateSyncedRows` sort bug | Устраняет деградацию сетки | Минимальная |
| Wire syncedRowHeights в DxfHeadlessRunner | Основной фикс | Минимальная |
| Увеличить `MaxStockForSolver` cap (150→200+) | ~1-2% перерасхода | Низкая |
| Режим "Stock Flush" на последнем фасаде | ~3-5% экономии на остатках | Средняя |
| Диагностика "мёртвого" склада по классам | Видимость проблемы | Низкая |

---

## Tasks

### Фаза 1 — Критические баги Row Sync

#### Task 1: Исправить порядок рядов в CreateSyncedRows

**Файл:** `Preprocessor.cs`

**Проблема:** строка 99 сортирует высоты по убыванию, разрушая порядок рядов снизу-вверх
(ряд с нестандартной высотой может оказаться не на своём месте в фасаде).

**Что сделать:**
- Удалить `syncedHeights = syncedHeights.OrderByDescending(h => h).ToList();`
- Сохранить исходный порядок — Фасад 1 уже возвращает ряды от нижнего к верхнему
- Добавить guard: если переданный список пуст — бросить ArgumentException с ясным сообщением
- Лог: `[ROW-SYNC] CreateSyncedRows: {count} рядов, высоты (снизу вверх): [{h1}, {h2}, ...]`
- Лог: `[ROW-SYNC] Ряд {i}: Y={y:F0}, H={h:F0}`

**Ограничение:** Не трогать логику `while (remainingHeight > 0)` — она правильно добавляет
стандартные ряды, если текущий фасад выше Фасада 1.

#### Task 2: Подключить Row Sync в DxfHeadlessRunner

**Файл:** `tools/DxfHeadlessRunner/Program.cs`

**Что сделать:**
- После обработки Фасада 1 (после `input.Rows` заполнены): сохранить
  `List<double> syncedRowHeights = input.Rows.Select(r => r.Height).ToList()`
- Объявить `syncedRowHeights` перед циклом (изначально `null`)
- В цикле: при `facadeIndex > 0` передавать `syncedRowHeights` в вызов
  `Preprocessor.DivideIntoRows(..., syncedRowHeights: syncedRowHeights)` (строка ~109-118)
- Для Фасада 1: после `input.Rows` заполнены — заполнить переменную
- Лог: `[ROW-SYNC] Захвачена сетка Фасада 1: {count} рядов → будет применена к Фасадам 2-{N}`
- Лог для каждого последующего фасада: `[ROW-SYNC] Фасад {i}: применяем сетку рядов от Фасада 1 (RowHeightForecaster пропущен)`
- Лог для Фасада 1: `[ROW-SYNC] Фасад 1: сетка рядов рассчитывается независимо (B2 активен)`

**Важно:** Высоты рядов выводить в итоговой строке после расчёта рядов Фасада 1:
```
[ROW-SYNC] Захвачена сетка: [600, 600, 600, 600, 600, 400] мм (снизу вверх)
```

#### Task 3: Валидация синхронизированных рядов против окон целевого фасада

**Файл:** `Preprocessor.cs`, метод `CreateSyncedRows`

**Проблема:** Если на Фасаде 2 окна расположены чуть иначе (по Y), граница ряда из Фасада 1
может попасть в запрещённую зону (< 130 мм от края окна). Нужна проверка.

**Что сделать:**
- Добавить параметр `List<WindowInfo>? windows` в `CreateSyncedRows`
- После создания рядов: для каждой границы ряда (`row.Y + row.Height`) проверить,
  что она не попадает в зону `(win.MinY - minY + safeMargin, win.MaxY - minY - safeMargin)` для каждого окна
- Если нарушение найдено: вернуть `null` вместо рядов
- В `DivideIntoRows`: если `CreateSyncedRows` вернул `null` — логировать WARN и запустить
  `RowHeightForecaster` как обычно (без синхронизации)
- Лог warn: `[ROW-SYNC WARN] Фасад {id}: граница ряда {y:F0} мм конфликтует с окном [{win.MinY:F0}-{win.MaxY:F0}]. Fallback на RowHeightForecaster.`

**Параметр `windows` — nullable:** если не передан — валидация пропускается (обратная совместимость).

---

### Фаза 2 — Минимизация склада (Path A, геометрический фильтр)

#### Task 4: Заменить эвристический SelectStockForSolver геометрическим фильтром

**Файлы:** `RollingHorizonEngine.cs`, `OrToolsModels.cs`

**Архитектурная проблема:**
`SelectStockForSolver` предфильтрует склад до `MaxStockForSolver` (80 шт.) по эвристическим
приоритетам (VIP bucket, narrow strips, exact match, byArea, byMatch). Остатки вне бюджета
**никогда не передаются** в `PatternGenerator` — солвер физически не может их выбрать.
Это ухудшает качество решения: пропускаются остатки, которые CP-SAT выбрал бы как оптимальные.

**Правильный подход:** единственный критерий отбора — **геометрическая совместимость**
(может ли остаток физически поместиться в текущий ряд). CP-SAT сам решает, что выгоднее
использовать, через `RemnantUsageWeight` в функции цели.

**Что сделать:**

1. Переработать тело `SelectStockForSolver` (сигнатуру сохранить для совместимости):
   - Удалить блоки: VIP bucket, narrowStrips bucket, exactMatch bucket, byArea/byMatch split
   - Новая логика:
     ```csharp
     var feasible = allStock
         .Where(r => CanUseRemnant(r, rowHeight, constraints))
         .ToList();
     // Мягкий performance-порог: передаём всё если < hardCap
     int hardCap = _config.MaxStockForSolver; // теперь = 400 (аварийный лимит)
     if (feasible.Count <= hardCap) return feasible;
     // Fallback при аномально большом складе: только сортировка по площади
     return feasible.OrderByDescending(r => r.Area).Take(hardCap).ToList();
     ```

2. Сохранить только virtualOnes (VirtualCutout) — они по-прежнему всегда включаются.

3. В `OrToolsModels.cs`:
   - Изменить `MaxStockForSolver` default с `80` на `400`
   - Обновить комментарий: `MaxStockForSolver` — это performance hard cap, не качественный фильтр

4. В `DxfHeadlessRunner/Program.cs`:
   - Убрать адаптивный расчёт `adaptiveStockCap` (больше не нужен для качества)
   - Использовать фиксированный `config.MaxStockForSolver = 400` для всех фасадов

5. Логирование:
   - `[STOCK-FILTER] Ряд {i}: склад {total} шт. → геом.совместимых {feasible} шт. → передано солверу {passed}`
   - При срабатывании hard cap: `[STOCK-FILTER WARN] Ряд {i}: feasible {n} > hardCap {cap}, fallback сортировка по площади`

**Что НЕ меняем:** `CanUseRemnant` — корректный геометрический фильтр, оставляем.

#### Task 4b: Замер производительности PatternGenerator

**Файл:** `RollingHorizonEngine.cs`

**Проблема:** Неизвестно, как изменится время генерации паттернов при передаче 150-200+ остатков
вместо 80. Нужны данные для настройки `MaxStockForSolver` hard cap.

**Что сделать:**
- Обернуть вызов `PatternGenerator.GeneratePatterns()` в `Stopwatch`
- Лог: `[PERF] Ряд {i}: PatternGenerator {ms}мс, паттернов={count}, остатков={stockCount}`
- Расширить лог до разбивки по фазам: `phase2single={p2} phase2multi={p2m} phase3={p3} phase4={p4}`
- Если `ms > 3000` — логировать WARN: `[PERF WARN] Ряд {i}: PatternGenerator {ms}мс — рекомендуется снизить MaxStockForSolver`

**Ограничение:** Только лог, не менять логику генерации паттернов.

#### Task 4c: Настройка MaxStockForSolver как performance-параметра

**Файл:** `OrToolsModels.cs`, документация в коде

**Что сделать:**
- Обновить XML-комментарий к `MaxStockForSolver`:
  ```csharp
  /// <summary>
  /// Аварийный лимит производительности: макс. остатков, передаваемых PatternGenerator.
  /// Геометрический фильтр (CanUseRemnant) применяется ПЕРВЫМ; этот лимит срабатывает
  /// только если геом.совместимых остатков больше threshold.
  /// Рекомендуемый диапазон: 300-500 (зависит от числа сегментов на ряд и таймаута).
  /// Увеличивать если [PERF WARN] не появляется, уменьшать если [PERF WARN] слишком частый.
  /// </summary>
  public int MaxStockForSolver { get; set; } = 400;
  ```

#### Task 5: Диагностика "мёртвого" склада после каждого фасада

**Файл:** `tools/DxfHeadlessRunner/Program.cs`

**Что добавить:** После обновления склада (после `stock.AddRange(savedRemnants)`) выводить:
```
[STOCK-DIAG] Фасад {i}: склад {total} шт.
  → Класс A (≥200мм) : {countA} шт. ({areaA:F3} м²) — пригодны для любых рядов
  → Класс B (150-199мм): {countB} шт. ({areaB:F3} м²) — только оконные зоны
  → Мёртвые (<150мм)  : {countDead} шт. ({areaDead:F3} м²) — НЕ могут быть использованы
  → "Мёртвый" склад (% от общей площади): {deadPct:F1}%
```
- Ввести локальный метод `PrintStockDiag(List<Remnant> stock, int facadeIndex)` для переиспользования

#### Task 6: "Stock Flush" режим для последнего фасада

**Файл:** `tools/DxfHeadlessRunner/Program.cs`

**Идея:** Последний фасад запускается без Row Sync — `RowHeightForecaster` использует полный
склад для подбора оптимальных высот рядов (B2 максимально активен). Это жертвует визуальной
синхронизацией **только** последнего фасада ради максимальной утилизации материала.

**Что сделать:**
- Добавить в начало Program.cs: `bool stockFlushLastFacade = true;` (конфигурационный флаг)
- При формировании рядов последнего фасада (`facadeIndex == facades.Count - 1 && stockFlushLastFacade`):
  передать `syncedRowHeights: null` (Free mode)
- Лог: `[STOCK-FLUSH] Последний фасад: Row Sync отключён, B2 активен для максимальной утилизации остатков`
- Также: перед циклом распечатать конфигурацию:
  ```
  [CONFIG] Row Sync: ON (Фасады 2-{N-1}), Stock Flush Last Facade: {on/off}
  ```

---

### Фаза 2b — Полное покрытие мультиостаточных паттернов

#### Task 9: Адаптивный top-N для Phase 2b/2c/2d (мультиостаточные паттерны)

**Файл:** `PatternGenerator.cs`

**Архитектурная проблема:**
Phase 2b/2c/2d содержат захардкоженные `.Take(10)`, `.Take(15)`, `.Take(12)`.
После Task 4 (геометрический фильтр) в PatternGenerator попадут 150+ остатков,
но Phase 2b/2c/2d по-прежнему видят только топ-10/15/12 по ширине.
Остаток с рангом №16+ **никогда не будет в мультиостаточной комбинации**, даже если он
идеально закрывает нужную позицию в паттерне R+R+T+R+T+R+R.

**Примеры теряемых паттернов:**
- Склад: [1150мм(#1), 1100мм(#2), ..., 430мм(#16), 420мм(#17)] — два узких остатка на краях
- Phase 2d видит только топ-12, поэтому паттерн R(#16)+R(#17)+T никогда не генерируется
- CP-SAT не может выбрать оптимальное, если паттерна не существует

**Что сделать:**

1. Заменить `.Take(10)`, `.Take(15)`, `.Take(12)` на адаптивные значения:
   ```csharp
   // Phase 2b (два остатка рядом): было .Take(10)
   int phase2bTopN = Math.Min(availableRemnants.Count, Math.Max(20, availableRemnants.Count / 3));
   var topFor2b = availableRemnants.Take(phase2bTopN).ToList();

   // Phase 2c (≥3 чередование R→T→R→T): было .Take(15)
   int phase2cTopN = Math.Min(availableRemnants.Count, Math.Max(25, availableRemnants.Count / 3));
   var topFor2c = availableRemnants.Take(phase2cTopN).ToList();

   // Phase 2d (пары R+R по MaxConsecutiveRemnants=2): было .Take(12)
   int phase2dTopN = Math.Min(availableRemnants.Count, Math.Max(20, availableRemnants.Count / 3));
   var topFor2d = availableRemnants.Take(phase2dTopN).ToList();
   ```

2. Лог: `[PATTERN-TOPN] Сег {j}: 2b={n2b}, 2c={n2c}, 2d={n2d} (availableRemnants={count})`

**Ограничение:** `maxPatterns` (2500) — по-прежнему общий бюджет на все фазы. Увеличение top-N
генерирует больше кандидатов, но PatternGenerator остановится при достижении лимита.
Производительность контролируется Task 10 (перераспределение бюджета).

#### Task 10: Перераспределить бюджет фаз PatternGenerator

**Файл:** `PatternGenerator.cs`

**Проблема:** При 150+ остатках Phase 2 (single sweep) исчерпывает `phase2BudgetCap = phase1 + max(200, maxPatterns×2/3)` быстро. Phase 2c/2d запускаются последними и упираются в `maxPatterns` раньше, чем генерируют все нужные комбинации.

**Что сделать:**

1. Пересмотреть формулу `phase2BudgetCap`:
   ```csharp
   // Текущее: 2/3 от maxPatterns — много отдаёт single-sweep, мало мультиостаткам
   // Новое: делим поровну — single sweep + multi-remnant получают равные бюджеты
   int phase2SingleBudget = phase1PatternCount + Math.Max(150, maxPatterns / 2);
   int phase2BudgetCap = phase2SingleBudget; // Phase 2 использует до этой границы
   // Phase 2b/2c/2d используют оставшийся бюджет до maxPatterns
   ```

2. Добавить лог с разбивкой: 
   `[PATTERN-BUDGET] Сег {j}: budget_total={max} single={singleUsed} 2b={p2b} 2c={p2c} 2d={p2d} phase3={p3}`

3. Проверить: если Phase 2 генерирует мало паттернов (сегмент простой, остатков мало),
   сохранить минимальный бюджет `Math.Max(150, ...)` — не занижать ниже этого порога.

**Зависит от:** Task 9 (адаптивный top-N имеет смысл только с достаточным бюджетом для Phase 2c/2d).

---

### Фаза 3 — Тесты

#### Task 7: Тест CreateSyncedRows сохраняет порядок рядов

**Файл:** `tests/RowSyncTests.cs` (новый)

**Тест-кейсы:**
1. `CreateSyncedRows_PreservesOrder` — передать [600, 400, 600], убедиться что Y-координаты
   растут: [0→600, 600→400=1000, 1000→600=1600] (НЕ [600, 600, 400] после сортировки)
2. `CreateSyncedRows_HandlesTallerFacade` — если `facadeHeight` > sum(syncedHeights),
   добавляются ряды по 600 мм вверху
3. `CreateSyncedRows_HandlesShorterFacade` — если фасад короче — обрезаем верхние ряды

**Реализация:** unit-тест без AutoCAD, напрямую вызывает `Preprocessor.CreateSyncedRows`
через Reflection (если метод private) или сделать `internal` + `[InternalsVisibleTo]`.

#### Task 8: Интеграционный тест Row Sync в DxfHeadlessRunner

**Файл:** `tests/HeadlessRowSyncTests.cs` (новый)

**Тест:**
1. Запустить DxfHeadlessRunner на `tests/f_test.dxf` (один фасад — базовый)
2. Если есть `tests/4_fasada.dxf` — запустить и проверить, что строка
   `[ROW-SYNC] Фасад 2: применяем сетку рядов от Фасада 1` присутствует в stdout
3. Проверить что высоты рядов Фасада 2 совпадают с Фасадом 1 (парсинг лог-файла)

---

## Commit Plan

### Commit 1 (Tasks 1-3): Row Sync Bug Fixes
```
fix: row sync — исправлен порядок рядов CreateSyncedRows, подключена синхронизация в DxfHeadlessRunner

- Удалена деструктивная сортировка в CreateSyncedRows (порядок рядов снизу-вверх сохраняется)
- DxfHeadlessRunner передаёт syncedRowHeights с Фасада 1 на Фасады 2-N
- Добавлена валидация: граница ряда vs окна целевого фасада (fallback на RowHeightForecaster)
- Verbose-логирование: ROW-SYNC, ROW-SYNC WARN
```

### Commit 2 (Tasks 4-4c): Geometric Stock Filter
```
refactor: SelectStockForSolver → геометрический фильтр, MaxStockForSolver поднят до 400

- Удалены эвристические приоритеты (VIP, narrow strips, exact match, byArea/byMatch buckets)
- Новый критерий: все остатки, проходящие CanUseRemnant (геом. совместимость с рядом)
- MaxStockForSolver теперь аварийный performance-порог (400), не качественный лимит
- Добавлен [PERF] лог времени PatternGenerator для настройки порога
```

### Commit 3 (Tasks 5-6): Stock Diagnostics + Stock Flush
```
feat: диагностика мёртвых остатков, Stock Flush режим последнего фасада

- Детальная диагностика склада по классам A/B/Dead после каждого фасада
- Stock Flush: последний фасад использует B2 (свободный RowHeightForecaster) для минимизации остатков
```

### Commit 4 (Tasks 9-10): Multi-Remnant Pattern Coverage
```
feat: PatternGenerator — адаптивный top-N для Phase 2b/2c/2d, перераспределение бюджета фаз

- Phase 2b/2c/2d теперь видят max(20/25/20, stock/3) остатков вместо фиксированных 10/15/12
- Остатки с рангом >15 по ширине теперь участвуют в паттернах R+R+T+R+T+R
- Бюджет Phase 2 single-sweep снижен с 2/3 до 1/2 maxPatterns — освобождает место для 2c/2d
- [PATTERN-TOPN] и [PATTERN-BUDGET] логи для диагностики покрытия
```

### Commit 5 (Tasks 7-8): Tests
```
test: row sync unit-тесты и интеграционный тест DxfHeadlessRunner

- RowSyncTests: порядок рядов, высокий/низкий фасад
- HeadlessRowSyncTests: smoke-тест синхронизации на реальном DXF
```

---

## Риски и ограничения

| Риск | Вероятность | Митигация |
|---|---|---|
| Окна Фасада 2 конфликтуют с сеткой Фасада 1 | Низкая (окна на одних высотах у реального здания) | Task 3 — fallback на RowHeightForecaster |
| Stock Flush нарушает визуальную синхронизацию | Только если `stockFlushLastFacade = true` | Флаг, можно отключить |
| `CreateSyncedRows` internal — тест через Reflection | Средняя | Переименовать в internal, добавить InternalsVisibleTo |
| PatternGenerator замедляется при 150+ остатках | Средняя | Task 4b — замер [PERF]; hard cap 400 предотвращает OOM/timeout |
| CP-SAT решает дольше с большим пространством поиска | Низкая | `TimeoutPerWindowSeconds` уже ограничивает, fallback на feasible |
| Адаптивный top-N (Task 9) перегружает Phase 2c/2d | Средняя | Task 10 перераспределяет бюджет; `maxPatterns=2500` — твёрдый лимит |
| Phase 2c/2d с top-25 не успевают до конца бюджета | Низкая | [PATTERN-BUDGET] лог покажет какая фаза реально использует бюджет |

---

## Что НЕ входит в этот план

- Row Sync для реальной AutoCAD команды INS (отложено, только DxfHeadlessRunner)
- Изменение B2 алгоритма RowHeightForecaster внутри Фасада 1
- Изменение структуры (алгоритма) Phase 2c/2d — только top-N и бюджет, не логика рекурсии
- Изменение ограничения `MaxConsecutiveRemnants` (остаётся = 2, это требование ТЗ)
