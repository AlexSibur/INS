[← Устранение проблем](troubleshooting.md) · [Back to README](../README.md) · [Спецификация →](spec.md)

# История изменений

## v3.6.0 (09.04.2026) — Row Sync + Stock Minimization

### Row Sync: синхронизация сеток рядов между фасадами

**Файлы:** `Preprocessor.cs`, `tools/DxfHeadlessRunner/Program.cs`, `OrToolsModels.cs`

- **Исправлен баг в `CreateSyncedRows`**: сортировка `.OrderByDescending(h => h)` разрушала порядок рядов снизу-вверх от Фасада 1. Теперь порядок сохраняется точно.
- **DxfHeadlessRunner**: после Фасада 1 захватывает его сетку рядов (`syncedRowHeights`) и применяет её ко всем последующим фасадам — горизонтальные швы «опоясывают» здание без ступенек.
- **Валидация конфликтов**: при синхронизации проверяется, что ни одна граница ряда не попадает в запрещённую зону (< 130 мм от края окна). При конфликте — fallback на независимый `RowHeightForecaster` с диагностическим предупреждением `[ROW-SYNC WARN]`.
- **Логирование**: `[ROW-SYNC]` при захвате и применении сетки, `[CONFIG]` при старте — показывает режим Row Sync и Stock Flush.

### SelectStockForSolver → геометрический фильтр

**Файл:** `RollingHorizonEngine.cs`, `OrToolsModels.cs`

- **Удалены эвристические приоритеты**: блоки VIP bucket, narrowStrips bucket, exactMatch bucket, byArea/byMatch split — все заменены единственным критерием: геометрическая совместимость (`CanUseRemnant`).
- **Солвер видит все подходящие остатки** без человеческого фильтра. CP-SAT сам выбирает оптимальные через `RemnantUsageWeight` + `STOCK_BONUS_MULTIPLIER`.
- **`MaxStockForSolver` поднят с 80 до 400** — теперь это аварийный performance-порог, срабатывающий только при аномально большом складе (> 400 геом.-совместимых остатков).
- **Лог:** `[STOCK-FILTER]` — сколько остатков передано солверу; `[STOCK-FILTER WARN]` при срабатывании hard cap.

### PatternGenerator: адаптивный top-N для мультиостаточных фаз

**Файл:** `PatternGenerator.cs`

- **Phase 2b/2c/2d** — top-N больше не захардкожен (было 10/15/12). Теперь `max(20/25/20, stock/3)` — адаптируется к размеру склада. Остатки с рангом > 15 по ширине теперь участвуют в паттернах `R+R+T+R+T+R+R`.
- **Бюджет Phase 2 single-sweep** снижен с 2/3 до 1/2 `maxPatterns` — освобождает место для Phase 2c/2d (мультиостаточные комбинации).
- **Лог:** `[PATTERN-TOPN]` — adaptive top-N для каждого сегмента; `[PATTERN-BUDGET]` — разбивка паттернов по фазам; `[PERF]` — время `PatternGenerator` по каждому ряду.

### Диагностика склада + Stock Flush

**Файл:** `tools/DxfHeadlessRunner/Program.cs`

- **`PrintStockDiag`** — после каждого фасада выводит состав склада по классам:
  - Класс A (≥ 200 мм) — пригодны для любых рядов
  - Класс B (150–199 мм) — только оконные зоны
  - Мёртвые (< 150 мм) — не могут быть использованы
- **Stock Flush** — последний фасад обрабатывается без Row Sync: `RowHeightForecaster` получает весь накопленный склад и подбирает высоты рядов для максимальной утилизации остатков (`B2` режим). Контролируется флагом `stockFlushLastFacade = true`.

### Тесты

**Файлы:** `tests/RowSyncTests.cs`, `tests/HeadlessRowSyncTests.cs`

- Unit-тесты `CreateSyncedRows`: порядок рядов, адаптация к высокому/низкому фасаду.
- Интеграционный тест: Row Sync на 4-фасадном DXF, проверка `[ROW-SYNC]` в stdout.

### Метрики (4_fasada.dxf до/после)

| Метрика | v3.5.2 | **v3.6.0** |
|---------|--------|------------|
| Высоты рядов (все фасады) | разные (B2 каждый раз) | **одинаковые** (Row Sync) |
| MaxStockForSolver | 80 (эвристика) | **400 (геом. фильтр)** |
| Остатков видит солвер | 80 из 200+ | **все геом.-совместимые** |
| Паттерны R+R+T с остатками > top-15 | ❌ не генерировались | **✅ генерируются** |

---

## v3.5.2 (29.03.2026) — Трассировка остатков от ряда к ряду

### Models.cs: новое поле `Remnant.CreatedAtRow`

- Добавлено поле `CreatedAtRow` (int, default `-1`) — индекс ряда, в котором создан остаток.
- Значение `-1` означает начальный склад (остаток загружен из БД, а не создан в текущем прогоне).
- Устанавливается во всех 6 точках создания остатков: `UpdateStockAfterRow` (4 пути: Height/Width/HeightCut/WidthCut + narrow-strip rotated) и `SaveCutoutAsRemnant` (L-boot cutouts).

### RollingHorizonEngine.cs: диагностика `[ROW-FLOW]`

- Каждый созданный остаток логируется с тегом `[ROW-FLOW] Created W×H (ShortId=…) at row N src=…`.
- В конце обработки ряда выводится `[ROW-FLOW] Row N: K remnants created → next rows: …`.
- При использовании свежего остатка (созданного ≤ 3 рядов назад): `[ROW-FLOW] Row N USED fresh remnant …`.
- `SelectStockForSolver` логирует количество свежих остатков в итоговой строке DEBUG.

### PatternGenerator.cs: диагностика `[FRESH]`

- Перед `return patterns` выводится `[FRESH] Row N Seg S: K fresh remnant(s) → F patterns generated`.
- «Свежими» считаются остатки с `CreatedAtRow >= 0` и `CreatedAtRow >= row.RowIndex − 2`.
- Позволяет отследить, какие паттерны реально используют недавно созданные остатки.

### Метрики (4_fasada.dxf, после исправлений)

| Фасад | Перерасход |
|-------|-----------|
| 1 | 2,1% |
| 2 | 14,1% |
| 3 | 6,1% |
| 4 | 15,4% |
| **Итого** | **9,41%** |

На складе: 53 полосы 1200×200 мм (исходно 97 шт.).

---

## v3.5.1 (29.03.2026) — Исправление перерасхода материала (7 критических правок)

### RemnantManager: исправлен алгоритм нарезки и выбора остатков

- **CreateRemnantsFromCut**: убрано дублирование угла — теперь из одного реза создаётся максимум 2 остатка (горизонтальная полоса справа + вертикальная полоса снизу). Было 3 остатка с перекрывающимся «угловым» куском.
- **MergeRemnants**: отключено — слияние создавало «физически несуществующий» остаток, завышая площадь склада.
- **FindBestRemnant**: снижен порог утилизации с **25% → 8%** — больше остатков проходят фильтр и предлагаются солверу.

### RemnantDatabase: исправлена антидубликатная логика

- **AddRemnant**: убрана проверка по временны́м меткам (ложные блокировки). Дубли теперь определяются только по GUID.

### PatternGenerator: расширен бюджет sweep-фазы

- **Фаза 2 (sweep)**: бюджет увеличен с **1/3 → 2/3** от `maxPatterns` (минимум 200 паттернов). Больше позиций остатков рассматривается солвером.

### OrToolsOptimizer + RollingHorizonEngine: пересчитаны веса

- `RemnantUsageWeight`: **10 → 100** (pass1) и **20 → 120** (pass2) — остатки получают значимый сигнал без превышения штрафа за новую плиту (`TileWeight = 10 000`).
- Добавлен `STOCK_BONUS_MULTIPLIER = 50` — перемножает бонус за каждый использованный остаток (max ~3 600 vs TileWeight 10 000).

### RollingHorizonEngine: подключён HorizonRows + fallback

- Реализован `HorizonRows` (по умолчанию **1**) — окно Rolling Horizon теперь настраивается.
- При `HorizonRows > 1` и таймауте — автоматический fallback к `horizon = 1` без регенерации паттернов.
- Исправлено значение по умолчанию `RollingHorizonRows` в `OrToolsModels.cs`: **3 → 1** (предотвращает зависания в DxfHeadlessRunner).

---

## v3.5.0 (28.03.2026) — Упрощение Excel-отчёта и автоматизация деплоя

### Excel: Лист «Статистика» — секция Б упрощена

- **Убрано:** 3 отдельные строки отходов (от новых, от остатков, на выброс) и «Коэффициент использования»
- **Добавлено:** материальный баланс:
  - Площадь утепления (нетто) — база бюджета (фасад минус окна)
  - Закупка нового материала — сколько реально купили
  - Остатки на складе — площадь остатков
  - Отходы — общая площадь всех отходов (одна строка)
  - **Перерасход = (Закупка / Нетто − 1) × 100%** — относительно площади утепления нетто
- Формула перерасхода включает всё: и остатки, и отходы — т.к. всё это часть закупленного материала
- Секции В (Материальный баланс) и Г (Проверка перерасхода) сохранены без изменений

### Excel: Лист «Сводная» — добавлен в fallback-путь

- `PluginCommands.GenerateExcelViaExternalProcess` теперь сериализует `AllTiles` в `excel_input.json` — лист «Сводная» генерируется и через ExcelGenerator.exe
- Удалены колонки «Сегмент» и «Позиция X» из ExcelGenerator (синхронизация с RemnantExcelExporter)

### Excel: Исправлены нули на листе «Статистика»

- ExcelGenerator делил площади (NewTilesArea, ReusedArea и т.д.) на 1 000 000, хотя они уже в м² — убрано
- Добавлены недостающие поля в PluginCommands: WindowsCount, WasteDisposalCount/AreaM2, CreatedRemnantsCount и т.д.
- StatsDto в ExcelGenerator дополнен: WindowsCount, OverConsumptionExceeded

### ClosedXML: Исправлена загрузка в AutoCAD

- `PluginInitializer.PreloadExcelAssemblies()` переведён на `AssemblyLoadContext.Default.LoadFromAssemblyPath()` (.NET 8)
- Устраняет `FileLoadException (0x80131621)` при генерации Excel внутри AutoCAD
- Fallback на `Assembly.Load(byte[])` сохранён

### Deploy: Устойчивый деплой при заблокированных файлах

- `deploy.ps1` при блокировке файла AutoCAD переименовывает старый в `.bak`, копирует новый
- ExcelGenerator.exe и его зависимости включены в деплой
- `InsulationMasterPro.csproj` исключает `tools\ExcelGenerator\**` из сборки основного проекта

### Автоматизация: dev.ps1

- Новый скрипт `dev.ps1` — цикл разработки в одну команду:
  - `.\dev.ps1 -Drawing "tests\f_test.dxf"` — сборка + закрытие AutoCAD + деплой + запуск с чертежом
  - `.\dev.ps1 -NoRestart` — только сборка + деплой
  - Graceful close AutoCAD (30 сек) → force kill при зависании

### Путь сохранения: аудит C:\DB_INS

- Все файлы (remnants.json, remnants.xlsx, diagnostics.txt/json, database.log) → `C:\DB_INS\`
- Устаревший ExcelGenerator.exe с путём `C:\БД остатков\` заменён актуальной версией

### Тест
- `f_test.dxf`: OPTIMIZATION PASS, TECH REQUIREMENTS PASS, перерасход 2.1%, 154 новых + 68 повторных

---

## v3.4.1 (23.03.2026) — Очередность остатков (снизу вверх) и учёт L-boot

### Проблема
- **PostOptimize** после основного прохода мог подставлять в пере-оптимизируемый ряд остатки из **будущих** рядов (финальный склад), что нарушало хронологию «материал снизу вверх» и путало Excel/lineage.
- **L-boot (`LBoot_CutOut`)**: в `UpdateStockAfterRow` в список `createdRemnants` попадали **все** L-boot остатки, уже лежавшие на складе — дубликаты по рядам и искажённые связи для Postprocessor.

### Решение
- **`PostOptimize`** (`RollingHorizonEngine.cs`):
  - Склад для пере-решения ряда `idx`: только **начальный склад** + остатки, у которых `SourceBlockId` указывает на блок из рядов **ниже** `idx` (строго `srcRow < idx`). Остатки с неизвестным `SourceBlockId` в карте блоков **исключаются** (не «разрешаются вслепую»).
  - Кандидаты на пере-оптимизацию: после отбора top-N по отходам — **сортировка по `rowIdx` снизу вверх**.
  - При замене ряда: полная синхронизация `CurrentStock`, `AllCreatedRemnants`, `LBootElements`, маппинг `blockId → rowIndex`.
- **L-boot в `createdRemnants`**: перед `ExtractLBootsAndCreateRemnants` фиксируются ID существующих L-boot на складе; в `created` добавляются только **новые** L-boot за текущий ряд; глобальное добавление «всех L-boot из склада» в `UpdateStockAfterRow` удалено. Аналогично в ветке **PostOptimize** после ре-решения ряда.

### Результат
- Физический поток и отчёты согласованы: остаток ряда `Y` не используется в ряду с меньшим номером при пост-оптимизации.
- Lineage в `Postprocessor` и лист «Сводная» в Excel не получают ложных «остатков из будущего ряда».

### Тест
- `f_test.dxf`: OPTIMIZATION PASS, TECH REQUIREMENTS PASS, типичный перерасход ~2.1% (после ограничения PostOptimize может быть чуть выше, чем при «глобальном» складе).

### Документация
- Синхронизированы README, AGENTS, architecture, commands, testing, troubleshooting, `.ai-factory/*`, версия в `DiagnosticLogger` → **3.4.1**.
- Excel: в коде `RemnantExcelExporter.UpdateWithMovement` создаётся **3 листа** (Статистика, Движение остатков, Сводная); ранее в текстах фигурировало «4 листа» — исправлено.

---

## v3.4.0 (23.03.2026) — Intra-Row BinPack консолидация

### Проблема (была в v3.3.0)
Мелкие блоки (~200 мм) в каждом сегменте ряда потребляли по отдельной полной плите 1200 мм: например, `1.4.001` (200 мм) и `1.4.003` (200 мм) в одном ряду открывали две плиты, создавая два остатка по 1000 мм — прямой перерасход.

Причина: солвер считал `NewTileCount` независимо для каждого сегмента. Механизма объединения резов в рамках одной физической плиты не существовало.

### Решение — `IntraRowConsolidate` (BinPack post-processor)

**Файл:** `RollingHorizonEngine.cs`, метод `IntraRowConsolidate`

После того как CP-SAT солвер коммитит ряд, пост-процессор:

1. Собирает все частичные `[New]` блоки (ширина < 1200 мм) из **всех сегментов ряда**
2. Сортирует по убыванию ширины (BFD — Best Fit Decreasing)
3. Упаковывает в минимальное число физических плит 1200 мм (Best Fit Decreasing bin-packing)
4. Для каждого бина с >1 блоком:
   - **anchor-блок** (первый/самый широкий) → остаётся `[New]`
   - остальные → конвертируются в `[CutRemnant]` через цепочку виртуальных остатков
5. Перед конвертацией — проверка **C13**: если конвертация создала бы более `MaxConsecutiveRemnants=2` последовательных `[CutRemnant]` блоков в сегменте — блок пропускается (остаётся `[New]`)
6. После `UpdateStockAfterRow` — удаление дублирующих остатков anchor-блоков и промежуточных остатков виртуальной цепочки

**Ключевое свойство:** `EnableIntraRowConsolidate = true` (по умолчанию включён).

### Row-level objective (экспериментальная опция)

**Файл:** `OrToolsOptimizer.cs`, метод `BuildObjective`

Добавлен режим `OptimizerConfig.EnableRowLevelObjective = true`, при котором солвер использует `ceil(Σ NewWidth_ряда / 1200) × TileWeight` вместо `Σ(NewTileCount_сегмента × TileWeight)`. Корректно моделирует физические плиты на этапе решения. На практике IntraRowConsolidate уже исправляет ситуацию пост-фактум, поэтому дополнительного прироста не даёт. Оставлен как опция (`EnableRowLevelObjective = false` по умолчанию).

### Результаты на f_test.dxf (15770×10001 мм, 12 окон)

| Метрика | v3.3.0 | **v3.4.0** | Δ |
|---------|--------|------------|---|
| NewTilesCount | 160 | **152** | **-8 (-5%)** |
| Reused remnants | 52 | **71** | **+19 (+37%)** |
| Overconsumption% | 6.1% | **0.8%** | **-5.3× лучше** |
| Waste% | 1.8% | **1.6%** | −0.2 пп |
| КПД раскроя | 74.4% | **77.3%** | +2.9 пп |
| Создано остатков | 74 шт. | **65 шт.** | -9 шт. |
| SolveTimeMs | 11 139 | **6 847** | **-39%** |
| C13 ConsecRemnants | ✅ PASS | ✅ PASS | — |

---

## v3.3.0 (23.03.2026) — Sweep-перебор остатков и снижение перерасхода

### Генерация паттернов (PatternGenerator)
- **Sweep 10 мм**: эвристики A–J заменены одним циклом `offset = 0, 10, 20 … segmentWidth-rw`. Позиция остатка определяется только технологическими ограничениями (MinBlock, MinRelease, MinJointOffset), без привязки к кратным 600/1200 мм
- **Предфильтр**: заведомо невалидные позиции (prefix < minFirstBlock, суффикс < minLastBlock) пропускаются до вызова `TryHeuristicPattern` — экономит бюджет
- **Phase 2b (пары)**: первый остаток — sweep 10 мм, второй вплотную за ним; пара может оказаться в любом месте ряда
- **Бюджет Phase 2**: ограничен 1/3 от maxPatterns — гарантирует Phase 3 (подстановка точных обрезков) получит достаточно бюджета

### Высоты рядов (RowHeightForecaster)
- **Two-pass dead zone**: Pass 1 — жёсткий запрет высот 451–599 мм (`h == 600 OR h <= 450`). Pass 2 (fallback) — тяжёлый soft penalty при INFEASIBLE
- **Ограничение активных рядов**: `maxActiveRows = ceil(H/TileHeight) + 2` предотвращает расширение в тонкие лишние ряды
- **Таймаут hard-ban**: `min(15 сек, baseTimeout × 3)` — солвер успевает найти оптимальное решение без dead zone

### Веса и бюджеты (RollingHorizonEngine)
- **adaptiveMaxPat**: 400 → **10 000** (единый лимит, без тройки порогов)
- **adaptiveTimeout**: `max(15, TimeoutPerWindowSeconds × 3)`
- **RemnantUsageWeight**: pass1 5→10, pass2 10→20

### Excel-отчёт (RemnantExcelExporter)
- **Лист «Статистика»**: все поля на русском (FacadeGrossArea → «Площадь фасада (брутто)» и т.д.)
- **Перерасход с учётом склада**: новая строка `(TotalNew − StockArea) / NetArea − 1`
- **Секция Г «Проверка перерасхода»**: 7 строк с поэлементной разбивкой (нетто, плиты на фасаде, остатки, отходы, итого, контроль, формула)
- **Лист «Сводная»**: удалены столбцы «Сегмент» и «Позиция X» (данные — только в diagnostics.txt)
- **Лист «Диагностика»**: удалён (данные — только в diagnostics.txt)
- Итого **3 листа** в полном отчёте после INS: Статистика, Движение остатков, Сводная (ранее до 6; убраны лишние; данные диагностики — в TXT/JSON)

### Маркировка (VisualizationEngine, LayoutEngine)
- Обновлены XML-комментарии `BlockNumber` и `FormatAnnotation` для формата X.Y.NNN
- Диагностическое предупреждение при пустом `BlockNumber` во время визуализации

### Результат на f_test.dxf
- 160 плит (было 167), 52 переиспользовано, **перерасход 6,1%** (было 10,7%)
- Время solve: 11–12 сек (было 5 сек)
- 0 рядов в мёртвой зоне 451–599 мм

## v3.2.0 (21.03.2026) — Оптимизация качества и материальный учёт

### Оптимизация RowHeightForecaster
- **Dead zone → soft penalty**: жёсткий запрет высот 451-599 мм заменён мягким штрафом. Солвер может выбрать любую высоту ≥ 150 мм, но предпочитает высоты с сохраняемыми остатками. Устранён дефект: ряд 581 мм больше не разбивается на 431+150, экономия ~12 плит
- **RemnantUsageWeight** поднят с 3 до 5 (pass1), pass2 с 6 до 10 — солвер сильнее поощряет переиспользование остатков
- **Stock-driven heights**: при покрытии складом ≥ 80% ширины фасада награда удвоена (rewardScale 200 vs 80)
- **MaxStockForSolver** вынесен в `OptimizerConfig` (по умолчанию 50, было захардкожено 30)

### Качество оптимизации
- **B2: Small remnant penalty** — остатки < MinBlock (200 мм) при создании штрафуются x2 в `PatternGenerator`
- **Unused stock penalty** — неиспользованные остатки со склада учитываются в целевой функции `OrToolsOptimizer`

### Корректность
- **A1: Remnant tracking** — `ProcessingContext.UsedRemnantIds` для точного трекинга, `RemnantDatabase.UpdateAfterLayout` для атомарного обновления
- **A3: L-Boot shelf severity** — нарушение полки L-boot (< 150 мм) → ошибка (было предупреждение)
- **C7: GUID deduplication** — `RemnantDatabase.LoadRemnants` удаляет дубликаты GUID
- **C9: CleanupDatabase** — автоматическая очистка старых остатков при запуске (> 90 дней)

### Диагностика
- **C3: ConsecRemnants split** — раздельный учёт для глухих стен (≤ 2) и оконных сегментов (≤ 3)
- **C4: Multi-facade monitoring** — `Δ склад` (шт./м²) с предупреждением при росте
- **C5: WastePct** — процент отходов в материальном балансе (`LayoutResult.WastePct`)
- **C8: Dead Zone check** — удалён (больше не ограничение, заменён soft penalty)
- **C12/C12a validation** — явная проверка Stagger и CornerZone с min-значениями
- **C6: PieceFate** — enum `InWall/InStock/Waste` на каждом остатке

### Headless Runner
- **ClearAll** перед каждым запуском — тест всегда стартует с чистого склада
- Результат f_test.dxf: 165 плит, 9.4% перерасход, 17 рядов

### Прочее
- **LBootGenerator** помечен `[Obsolete]` — использовать `RollingHorizonEngine.ExtractLBootsAndCreateRemnants`

## v3.1.0 (21.03.2026) — Материальный баланс и headless-тестирование

### Материальный баланс
- **Postprocessor**: заполнение `Stats` и `MaterialBalance` из геометрии фасада
- **PluginCommands**: передача `facadeWidth`, `facadeHeight`, `windows[]` в `Postprocessor`
- **LayoutEngine.MergeResults**: копирование FacadeGrossArea, WindowsArea, WindowsCount
- **RemnantExcelExporter**: полный лист «Свод» с условным форматированием, дедупликация GUID в листе «Движение», лист «Диагностика» с группировкой
- **DiagnosticLogger**: §7 с полной разбивкой и строкой баланса, `diagnostics.json` — блок `materialBalance` (17 полей)

### Headless Runner
- **DxfHeadlessRunner**: headless-тестирование без AutoCAD (`tools/DxfHeadlessRunner/`)
- Текстовый парсер DXF (LWPOLYLINE) — без зависимости от netDxf
- `RollingHorizonEngine.Optimize(FacadeInput, List<Remnant>)` — новый overload для headless
- Pre-cut simulation: остатки из-под окон доступны до решения текущего ряда
- `try/catch InvalidProgramException` в Models.cs, Postprocessor.cs для headless

### Исправления
- **RemnantDatabase.AddRemnants**: пакетное добавление (1 бэкап вместо N)
- **Postprocessor.ConvertToLayoutResult**: HashSet дедупликация GUID в StockAfter
- **RowHeightForecaster**: жёсткий запрет H < 150 мм с fallback
- **ValidateOverlapBoundary**: warn+continue вместо throw при отрицательных координатах
- **RemnantDatabase.ClearAll**: убран двойной бэкап

### Миграция
- Путь БД: `C:\БД остатков\` → `C:\DB_INS\` во всех файлах

## v3.0.1 (21.03.2026) — ТЗ v13.9

- **BUG-7: CreatedAndReused в БД** — остатки `Создан и переиспользован` больше не сохраняются в `remnants.json`
- **BUG-8/9: Нумерация остатков** — чертёжная нумерация `N0015.1` вместо GUID
- **BUG-10: Разбивка перерасхода** — §7: категории (новые / раскрой / переиспользованные), ⚠️ > 7%
- **Движение остатков** — §8: «сохранены в БД» и «переиспользованы (не в БД)»

## v2.9 (20.03.2026) — ТЗ v13.8

- **DiagnosticLogger** — TXT-диагностика (10 секций) + JSON-компаньон
- **Путь**: `C:\DB_INS\diagnostics.txt` / `diagnostics.json`
- **Excel fallback** — если `.xlsx` заблокирован → `remnants_{timestamp}.xlsx`
- **Маркировка v2.8** — lineage `N0001.1` от родительской плиты

## v2.8 (20.03.2026) — ТЗ v13.7

- **RowHeightForecaster** — stock reward ≤ 80% от deviation penalty; 600 мм предпочтительна
- **Маркировка остатков** — формат `N0001.1` / `N0001.2`; fallback `R0001`
- **Lineage в БД** — snapshot `BaseBlockNumber`/`SuffixCounter`

## v2.7 (19.03.2026) — ТЗ v13.6

- **L-boot плиты** — аннотации с фактическими размерами (1200×210 вместо 1200×600)
- **DisplayNumber** — `N0001` / `R0001` с префиксом
- **Площади** — фактическая высота плитки вместо высоты ряда
- **Материальный баланс** — Excel-лист «Свод», перерасход %, КПД %
- **Текстовый отчёт** — секция «МАТЕРИАЛЬНЫЙ БАЛАНС»
- **Консоль AutoCAD** — краткий вывод перерасхода и КПД

## v2.6 (18.03.2026) — ТЗ v13.4

- **MText-аннотации 60 мм** — двустрочный формат (размер + номер)
- **Блочная нумерация** — N0001 (новые), R-0001 (остатки)
- **CutHistory** — полная трассировка нарезки
- **ParentRemnantId** — трассировка к родительскому остатку
- **Excel-отчёт** — `remnants.xlsx` с 5 листами
- **Dead zone penalty** — солвер избегает несохраняемых отходов
- **Height remnants** — корректное создание и сохранение
- **Sprint 3 тесты** — 23 теста суб-нарезки и трассировки

## v2.5 — Ряды переменной высоты

- Верхний ряд: фактическая высота (< 600 мм)
- Выбор максимальной высоты при наличии остатков
- Критерий: минимальная площадь остатков + look-ahead

## v2.4 — Запрет мелких блоков

- Блоки < 200 мм запрещены
- Приоритет крупных остатков (утилизация ≥ 25%)
- Блок выпуска = «короткий» (считается в макс. 2 подряд)

## v2.1–v2.3 — Выпуски и базовая оптимизация

- Выпуски за контур: 170 мм, чередование через ряд
- Штриховка перекрытий с окнами
- Раскрой остатков с возвратом в склад
- Шаг перебора: 10 мм, look-ahead: 10 рядов

---

## Аудит v11 (17.03.2026)

Ключевые исправления v11.0 → v11.1:

- **CornerZone logic** — знаки WindowOverlap исправлены: `leftOverlapEdge = w.MinX − overlap`, `rightOverlapEdge = w.MaxX + overlap`
- **Синхронизация тестовых моделей** — `OptimizationConstraints` с `WindowOverlap = 20`, `CornerZone = 150`, класс `WindowInfo`
- **DTO LBootElement** — Г-элементы как сериализуемая сущность (WindowId, Corner, ShelfH, ShelfV)
- **int vs double** — tech debt (ТЗ требует int, код использует double)
- Все 10 тестов Optimal, Exit Code 0

Соответствие ТЗ v11: C1, C2, C3, C12, C12a, C13, C16 (L-boot = 1 блок), WindowOverlap 20 мм, Release 170 мм — ✅.

## See Also

- [Архитектура](architecture.md) — алгоритм и ограничения
- [Тестирование](testing.md) — headless runner, диагностика
