# Insulation Master Pro v3.6.0

> Плагин AutoCAD для автоматизации раскладки плитного утеплителя на 2D-фасадах с оптимизацией остатков, OR-Tools CP-SAT солвером и Clipper2 boolean-геометрией.

## Основные возможности

- **Автоматическая раскладка** плит 1200×600 мм с перевязкой швов ≥ 100 мм
- **OR-Tools оптимизация** — CP-SAT солвер с Rolling Horizon, multi-pass и look-ahead
- **Управление остатками** — lineage-маркировка (формат `X.Y.NNN.ZZ`), суб-нарезка, приоритет крупных, `PieceFate` tracking
- **Intra-facade reuse** — остатки из-под окон переиспользуются в соседних сегментах и последующих рядах
- **Row Sync** — горизонтальные швы синхронизируются между фасадами; опциональный Stock Flush на последнем фасаде
- **Геометрический фильтр склада** — солвер видит все пригодные остатки без эвристических приоритетов; CP-SAT сам выбирает оптимальные
- **Адаптивная высота рядов** — любая высота ≥ 150 мм, soft-penalty оптимизация вместо жёстких запретов
- **Выпуски за контур** — 170 мм, чередование рядов, обрезка по окнам
- **Г-элементы (L-boot)** — Boolean Subtraction у оконных проёмов
- **Excel-отчёт** — 3 листа: Статистика (нетто/закупка/перерасход), Движение остатков, Сводная (X.Y.NNN.ZZ)
- **Полная диагностика** — TXT + JSON при каждом запуске (C1, C12, C12a, C13, CH)
- **Визуализация** — MText 60 мм, нумерация X.Y.NNN.ZZ, штриховка перекрытий

## Быстрый старт

### AutoCAD-плагин

```powershell
# Сборка + деплой
.\deploy.ps1

# Или полный цикл: сборка + закрытие AutoCAD + деплой + запуск
.\dev.ps1 -Drawing "tests\f_test.dxf"
```

В AutoCAD:
```
NETLOAD → InsulationMasterPro.dll → INS
```

### Headless-тестирование (без AutoCAD)

```powershell
dotnet run --project tools/DxfHeadlessRunner/DxfHeadlessRunner.csproj -c Release -- tests/f_test.dxf
```

Читает DXF, запускает полный pipeline (Preprocessor → PatternGenerator → RollingHorizonEngine → Postprocessor → DiagnosticLogger), выводит PASS/FAIL.

## Команды AutoCAD

| Команда | Описание |
|---------|----------|
| `INS` | Раскладка утеплителя |
| `INS_CHECK` | Проверка контуров |
| `INS_DEL` | Удалить раскладку |
| `INS_STOCK` | Статистика склада |
| `INS_STOCK_IMPORT` | Импорт начального склада из Excel |
| `INS_CLR` | Очистить склад |

## Диагностика

При каждом `INS` (и при headless-запуске) создаются файлы:

```
C:\DB_INS\diagnostics.txt    — 10 секций, человекочитаемый
C:\DB_INS\diagnostics.json   — зеркало для AI-анализа
C:\DB_INS\remnants.json      — база остатков
C:\DB_INS\remnants.xlsx      — Excel-отчёт (4 листа: Статистика, Движение остатков, Сводная, Нач. склад)
```

## Структура проекта

```
ver09042026/
├── InsulationMasterPro.csproj   # AutoCAD-плагин
├── Uteplitel.sln                # Solution
├── PluginInitializer.cs         # Entry point, native DLL resolver
├── PluginCommands.cs            # Команды INS, INS_CHECK и т.д.
├── Models.cs                    # Remnant, TileInfo, LayoutResult
├── OrToolsModels.cs             # FacadeInput, WindowInfo, OptimizerConfig
├── Preprocessor.cs              # Деление на ряды, сегменты
├── PatternGenerator.cs          # Генерация паттернов раскладки
├── RollingHorizonEngine.cs      # Rolling Horizon + multi-pass + pre-cut simulation
├── OrToolsOptimizer.cs          # CP-SAT constraint model
├── Postprocessor.cs             # Нумерация, баланс, валидация
├── VisualizationEngine.cs       # Отрисовка в AutoCAD
├── DiagnosticLogger.cs          # TXT + JSON диагностика
├── RemnantDatabase.cs           # JSON-персистенция (C:\DB_INS\)
├── RemnantExcelExporter.cs      # Excel (ClosedXML)
├── deploy.ps1                   # Сборка + деплой (handles locked files)
├── dev.ps1                      # Dev cycle: build + restart AutoCAD
├── tests/
│   └── f_test.dxf               # Тестовый фасад (15770×10001, 12 окон)
└── tools/
    ├── DxfHeadlessRunner/       # Headless тестирование без AutoCAD
    └── ExcelGenerator/          # Standalone Excel генератор (fallback)
```

## Документация

| Раздел | Описание |
|--------|----------|
| [Начало работы](docs/getting-started.md) | Установка, подготовка чертежа |
| [Команды](docs/commands.md) | INS, INS_CHECK — результаты, отчёты |
| [Архитектура](docs/architecture.md) | Pipeline, алгоритм, философия материала |
| [Тестирование](docs/testing.md) | Headless runner, анализ диагностики |
| [Устранение проблем](docs/troubleshooting.md) | Ошибки, советы, логирование |
| [История изменений](docs/changelog.md) | Версии v2.1..v3.5.0 |
| [Спецификация (ТЗ)](docs/spec.md) | Нормативное ТЗ v15.1, формальные ограничения, контракт данных |

## Системные требования

- **AutoCAD** 2025+ (для плагина; headless runner работает без AutoCAD)
- **.NET** 8.0 Windows
- **OR-Tools** 9.10, **Clipper2** 1.3.0, **ClosedXML** 0.105.0

## Лицензия

Проприетарное ПО.

---

**Спецификация:** [ТЗ v15.1](docs/spec.md) | **Changelog:** [v3.6.0](docs/changelog.md)
