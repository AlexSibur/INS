[← Архитектура](architecture.md) · [Back to README](../README.md) · [Устранение проблем →](troubleshooting.md)

# Тестирование и диагностика

## Headless-тестирование (без AutoCAD)

Проект включает headless runner для тестирования pipeline без AutoCAD. Он читает DXF-файл, запускает полный цикл оптимизации и выводит PASS/FAIL.

### Запуск

```powershell
dotnet run --project tools/DxfHeadlessRunner/DxfHeadlessRunner.csproj -c Release -- tests/f_test.dxf
```

### Пересборка после изменений

```powershell
dotnet build tools/DxfHeadlessRunner/DxfHeadlessRunner.csproj -c Release
```

### Быстрый цикл разработки (с AutoCAD)

```powershell
# Сборка + деплой + перезапуск AutoCAD с чертежом
.\dev.ps1 -Drawing "tests\f_test.dxf"

# Сборка + деплой + перезапуск + авто-запуск INS
.\dev.ps1 -Drawing "tests\f_test.dxf" -AutoRun "INS"

# Только сборка + деплой (без перезапуска AutoCAD)
.\dev.ps1 -NoRestart
```

### Что делает runner

1. **Парсинг DXF** — текстовый парсер (без внешних библиотек) читает замкнутые LWPOLYLINE
2. **Определение фасада/окон** — наибольший контур = фасад, остальные внутри = окна
3. **Row Sync** — после Фасада 1 захватывает его сетку рядов и применяет ко **всем** Фасадам 2…N (угловое замыкание)
4. **Препроцессинг** — `Preprocessor.DivideIntoRows` + `ClassifyRow` + `CreateSegments`
5. **Оптимизация** — `RollingHorizonEngine` (построчно, pre-cut simulation, multi-pass)
6. **Постпроцессинг** — `Postprocessor.ConvertToLayoutResult` + валидация
7. **Диагностика** — `DiagnosticLogger.WriteDiagnostics` → `C:\DB_INS\`

### Exit-коды

| Код | Значение |
|-----|----------|
| 0 | PASS — решение найдено, тех. требования соблюдены |
| 2 | Нет аргументов или DXF не найден |
| 3 | В DXF нет замкнутых LWPOLYLINE контуров |
| 5 | Есть сегменты без паттернов |
| 6 | TECH REQUIREMENTS: FAIL — критические ошибки E_* |
| 7 | OPTIMIZATION: FAIL — солвер не нашёл решение |

### Вывод консоли

Если DXF содержит несколько фасадов, runner обрабатывает их последовательно с единым пулом остатков:

```
[HEADLESS] Loading DXF: ...\tests\4_fasada.dxf
[HEADLESS] Found 4 facades and 48 windows
[HEADLESS] Склад очищен один раз перед всеми фасадами (общий пул остатков)

[HEADLESS] === Processing Facade 1/4 ===
[HEADLESS] Facade 1: 15770 x 10001 mm
[HEADLESS] Склад на входе: 0 остатков (0,000 м²)
[HEADLESS] Rows: 17, Segments: 41
[HEADLESS]   Высоты рядов: 400мм×1, 600мм×16
[HEADLESS] Starting optimization for facade 1...
[HEADLESS]   Движение склада: вход=0  -0исп  +101созд  =101итого  (29,xxx м²)
[HEADLESS] Facade 1 Results:
[HEADLESS]   SolverStatus: Optimal, SolveTimeMs: ~10000
[HEADLESS]   NewTiles: ~159, Reused: ~65
[HEADLESS]   Overconsumption: 5,4% (limit-exceeded=False)
[HEADLESS]   OPTIMIZATION: PASS
[HEADLESS]   TECH REQUIREMENTS: PASS

... (фасады 2–4) ...

╔═══════╦════════════╦════════════╦════════════╦══════════╦══════════╦══════════╦═══════════════╗
║ Фасад ║  Перерасх  ║  Куплено   ║  Нетто м²  ║ Скл.вход ║  -Исп    ║  +Созд   ║  Тех.ошибки   ║
╠═══════╬════════════╬════════════╬════════════╬══════════╬══════════╬══════════╬═══════════════╣
║ 1     ║ 5,4%       ║ 114,48 м²  ║ 108,58 м²  ║ 0        ║ -0       ║ +101     ║ PASS          ║
║ 2     ║ 2,8%       ║ 111,60 м²  ║ 108,58 м²  ║ 101      ║ -37      ║ +120     ║ PASS          ║
║ 3     ║ 3,4%       ║ 112,32 м²  ║ 108,58 м²  ║ 184      ║ -41      ║ +119     ║ PASS          ║
║ 4     ║ 3,4%       ║ 112,32 м²  ║ 108,58 м²  ║ 262      ║ -47      ║ +119     ║ PASS          ║
╚═══════╩════════════╩════════════╩════════════╩══════════╩══════════╩══════════╩═══════════════╝

  ОБЩИЙ ПЕРЕРАСХОД (4 фасада): 3,77%
  На складе после всех фасадов: 334 остатков, ~95 м²
```

При успешном Row Sync начало каждого фасада выглядит так:

```
[CONFIG] Row Sync: ON (Все фасады 2-N), Stock Flush Last Facade: OFF
[ROW-SYNC] Фасад 1: сетка рядов рассчитывается независимо (B2 активен, учитывает окна ВСЕХ фасадов)
[ROW-SYNC] Захвачена сетка: [600, 600, 600, 600, 600, 400] мм (снизу вверх)

[ROW-SYNC] Фасад 2: применяем сетку рядов от Фасада 1 (RowHeightForecaster пропущен)
  -- если окна конфликтуют с сеткой:
[ROW-SYNC WARN] Фасад Facade_2_...: граница ряда 1800 мм конфликтует с окном [1720-2800]. Fallback на RowHeightForecaster.
```

> **Примечание (v3.7.0):** Stock Flush отключён (`stockFlushLastFacade = false`). Все фасады, включая последний, используют Row Sync для соблюдения инварианта углового замыкания. Независимые высоты для последнего фасада нарушают стыковку на углах здания.

После каждого фасада — диагностика склада:

```
[STOCK-DIAG] Фасад 2: склад 213 шт.
  → Класс A (≥200мм) : 178 шт. (45,230 м²) — пригодны для любых рядов
  → Класс B (150-199мм):  24 шт. ( 2,100 м²) — только оконные зоны
  → Мёртвые (<150мм)  :  11 шт. ( 0,320 м²) — НЕ могут быть использованы
  → "Мёртвый" склад (% от общей площади): 0,7%
```

**Примечание:** Runner очищает `remnants.json` **один раз** перед первым фасадом. Остатки от фасада N автоматически передаются на фасад N+1 (единый `currentStock`).

**Однофасадный DXF** выводит только блок одного фасада без итоговой таблицы.

### Ключевые метрики для проверки

| Метрика | Хорошо | Плохо |
|---------|--------|-------|
| SolverStatus | Optimal / Feasible | Infeasible / Timeout |
| Reused | > 0 | 0 (остатки не переиспользуются) |
| Overconsumption (1 фасад) | ≤ 7% (идеал ~2-5%) | > 7% |
| Overconsumption (4 фасада) | ≤ 5% (идеал ~3.8%) | > 7% |
| TECH REQUIREMENTS | PASS | FAIL |
| -Исп (движение склада) | растёт от фасада к фасаду | стоит на 0 |

### Тестовые файлы

| Файл | Содержимое | Ожидаемый результат |
|------|-----------|---------------------|
| `tests/f_test.dxf` | 1 фасад, 15770×10001 мм, 12 окон | Overconsumption ≤ 7%, Exit 0 |
| `tests/4_fasada.dxf` | 4 фасада, по 12 окон каждый | Общий перерасход ≤ 5%, Exit 0 |

---

## Диагностические файлы

При каждом запуске `INS` (или headless runner) создаются в `C:\DB_INS\`:

| Файл | Формат | Назначение |
|------|--------|------------|
| `diagnostics.txt` | Human-readable | Полный отчёт, 10 секций |
| `diagnostics.json` | Machine-readable | Зеркало для AI/автоматического анализа |

Оба файла **перезаписываются** при каждом запуске.

## Диагностические теги (консоль DxfHeadlessRunner)

| Тег | Источник | Что означает |
|-----|----------|-------------|
| `[ROW-SYNC]` | Program.cs | Захват/применение сетки рядов от Фасада 1 |
| `[ROW-SYNC WARN]` | Preprocessor.cs | Граница ряда конфликтует с окном, fallback на RowHeightForecaster |
| `[CONFIG]` | Program.cs | Конфигурация Row Sync перед циклом (Stock Flush: OFF с v3.7.0) |
| `[STOCK-DIAG]` | Program.cs | Состав склада по классам A/B/Dead после каждого фасада |
| `[STOCK-FILTER]` | RollingHorizonEngine.cs | Геометрический фильтр: сколько остатков передано PatternGenerator |
| `[STOCK-FILTER WARN]` | RollingHorizonEngine.cs | Hard cap 400 сработал — сортировка по площади как fallback |
| `[PATTERN-TOPN]` | PatternGenerator.cs | Adaptive top-N для Phase 2b/2c/2d на каждый сегмент |
| `[PATTERN-BUDGET]` | PatternGenerator.cs | Разбивка паттернов по фазам (phase2single/multi/phase3/4) |
| `[PERF]` | RollingHorizonEngine.cs | Время `PatternGenerator` на ряд + счётчик паттернов |
| `[PERF WARN]` | RollingHorizonEngine.cs | PatternGenerator > 3000 мс — рекомендуется снизить MaxStockForSolver |
| `[ROW-FLOW]` | RollingHorizonEngine.cs | Трассировка остатков от ряда к ряду (CreatedAtRow) |
| `[VIP-PASS]` | RollingHorizonEngine.cs | (устарел v3.6.0) — удалён вместе с эвристическим фильтром |

## Тесты

### Unit-тесты (`tests/RowSyncTests.cs`)

```powershell
dotnet test
```

| Тест | Что проверяет |
|------|--------------|
| `CreateSyncedRows_PreservesOrder` | Порядок рядов снизу-вверх не нарушается |
| `CreateSyncedRows_HandlesTallerFacade` | При фасаде выше Фасада 1 добавляются ряды 600 мм |
| `CreateSyncedRows_HandlesShorterFacade` | При фасаде ниже Фасада 1 верхние ряды обрезаются |

### Интеграционный тест (`tests/HeadlessRowSyncTests.cs`)

Запускает DxfHeadlessRunner на `tests/4_fasada.dxf` и проверяет:
- Строка `[ROW-SYNC] Захвачена сетка` присутствует в stdout
- Строка `[ROW-SYNC] Фасад 2: применяем сетку` присутствует
- Высоты рядов Фасадов 2–3 совпадают с Фасадом 1

---

## 10 секций диагностики

| # | Секция | Что проверять |
|---|--------|--------------|
| 1 | Входные данные | Фасад, окна, склад — совпадают с чертежом |
| 2 | Параметры | Constraints: TileWidth=1200, MinBlock=200 и т.д. |
| 3 | Деление на ряды | Высоты (300..600), типы (Normal/BottomEdge/Middle/TopEdge) |
| 4 | Solver | Статус (Optimal), время решения |
| 5 | Раскладка | Все блоки с координатами, ширинами, типами (New/Reuse/Cut) |
| 6 | Маркировка | X.Y.NNN (плиты), X.Y.NNN.ZZ (остатки/lineage) |
| 7 | Материальный баланс | Перерасход %, КПД, экономия от остатков, проверка баланса |
| 8 | Склад (движение) | Использованные, созданные→БД, переиспользованные |
| 9 | Валидация | C1 MinBlock, C12 Stagger, C12a CornerZone, C13 ConsecRemnants, CH MinRowHeight(≥300) — pass/fail |
| 10 | Ошибки/Предупреждения | E_OVERCONSUMPTION, E_OVERLAP_IGNORED и т.д. |

## AI-анализ diagnostics.json

```json
{
  "version": "3.5.0",
  "input": { "facadeWidth": 15770, "windowCount": 12, ... },
  "solver": { "status": "Optimal", "solveTimeMs": 2489 },
  "balance": { "overconsumptionPct": 2.1, "cuttingEfficiencyPct": 77.3 },
  "stockAfter": {
    "createdSaved": [ { "displayNumber": "N0133.1", "width": 1000, ... } ],
    "createdAndReused": [ { "displayNumber": "N0034.2", ... } ]
  },
  "warnings": [ ... ]
}
```

Ключевые поля для проверки:
- `solver.status` — Optimal или Feasible
- `balance.overconsumptionPct` — цель ≤ 7%
- `balance.cuttingEfficiencyPct` — КПД раскроя
- `balance.stockSavingsPct` — экономия от остатков
- `errors[]`, `warnings[]` — критические нарушения

## See Also

- [Архитектура](architecture.md) — Pipeline, алгоритм
- [Команды](commands.md) — INS и результаты
- [Changelog](changelog.md) — история версий
