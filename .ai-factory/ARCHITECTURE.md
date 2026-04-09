# Architecture: Clean Architecture (AutoCAD Plugin)

## Overview

Проект InsulationMasterPro организован по принципам Clean Architecture,
адаптированным для AutoCAD-плагина на C#/.NET 8. Ключевая идея: вся доменная
логика (алгоритмы раскладки, оптимизация CP-SAT, управление остатками) не зависит
от AutoCAD API. AutoCAD-специфичный код сосредоточен исключительно во внешнем
слое Presentation. Это подтверждается работой `DxfHeadlessRunner`, который
запускает полный пайплайн без каких-либо ссылок на Autodesk.

## Decision Rationale

- **Project type:** Windows AutoCAD plugin (.NET 8, single DLL)
- **Tech stack:** C# 12, AutoCAD .NET API 2025+, OR-Tools 9.10, Clipper2 1.3.0
- **Key factor:** Сложная доменная логика (CP-SAT, Clipper2, многопроходная
  оптимизация) должна быть тестируемой и независимой от хоста (AutoCAD)

## Слои и соответствие файлам проекта

```
┌─────────────────────────────────────────────────────────┐
│              PRESENTATION (AutoCAD Layer)               │
│  PluginInitializer.cs   PluginCommands.cs               │
│  VisualizationEngine.cs  UserDialogService.cs           │
│  ← Единственный слой, который использует Autodesk.*    │
├─────────────────────────────────────────────────────────┤
│             APPLICATION (Pipeline Orchestration)        │
│  Preprocessor.cs         PatternGenerator.cs           │
│  RollingHorizonEngine.cs OrToolsOptimizationEngine.cs  │
│  OrToolsOptimizer.cs     OptimizationEngine.cs         │
│  LayoutEngine.cs         RowHeightForecaster.cs        │
│  Postprocessor.cs        RemnantManager.cs             │
│  GeometryUtils.cs (Clipper2 abstractions)              │
├─────────────────────────────────────────────────────────┤
│                 DOMAIN (Core Models)                    │
│  Models.cs              OrToolsModels.cs               │
│  ValidationEngine.cs    (C1–C17a правила)              │
│  ← Нет зависимостей на внешние библиотеки кроме BCL   │
├─────────────────────────────────────────────────────────┤
│              INFRASTRUCTURE (Persistence & IO)          │
│  RemnantDatabase.cs      RemnantExcelExporter.cs       │
│  ExcelMovementReporter.cs DiagnosticLogger.cs          │
│  ← JSON, ClosedXML, Newtonsoft.Json                    │
└─────────────────────────────────────────────────────────┘
```

## Dependency Rules

- ✅ Presentation → Application → Domain
- ✅ Infrastructure → Domain (реализует интерфейсы хранилищ)
- ✅ Application → Infrastructure (через интерфейсы / прямой вызов)
- ❌ Domain НЕ ссылается на Autodesk.*, OR-Tools, Clipper2, ClosedXML
- ❌ Application НЕ ссылается на Autodesk.* (AutoCAD API)
- ❌ Domain НЕ ссылается на Infrastructure напрямую
- ❌ Нельзя пробрасывать `Database`, `Transaction`, `Entity` AutoCAD через доменный слой

## Структура пайплайна (Application Layer)

Пайплайн всегда проходит стадии в строгом порядке:

```
[Presentation]
    PluginCommands (INS command)
        │
        ▼
[Application]
    Preprocessor           → валидация фасада, нарезка строк/сегментов
        │
        ▼
    PatternGenerator       → генерация кандидатов паттернов раскладки
        │
        ▼
    RollingHorizonEngine   → Rolling Horizon + симуляция предварительного реза
        │
        ▼
    OrToolsOptimizer       → CP-SAT модель с ограничениями
        │
        ▼
    Postprocessor          → нумерация, балансировка, CutHistory
        │
        ▼
[Presentation]
    VisualizationEngine    → рендер блоков в AutoCAD
        │
        ▼
[Infrastructure]
    DiagnosticLogger       → TXT + JSON отчёт
    RemnantDatabase        → сохранение остатков в C:\DB_INS\
    RemnantExcelExporter   → Excel (3 листа)
```

## Key Principles

1. **AutoCAD API только на границе** — классы, использующие `Autodesk.*`,
   это только `PluginInitializer`, `PluginCommands`, `VisualizationEngine`,
   `UserDialogService`. Всё остальное — чистый C#.

2. **Модели не содержат поведение AutoCAD** — `Models.cs` и `OrToolsModels.cs`
   описывают только бизнес-сущности (Remnant, TileInfo, FacadeInput).
   Никаких `ObjectId`, `Entity`, `Database` внутри моделей.

3. **Отказоустойчивость через fallback** — `OptimizationEngine` (жадный) является
   fallback для `OrToolsOptimizationEngine`. Пайплайн не должен падать при
   недоступности OR-Tools — деградирует к жадному алгоритму.

4. **Детерминированность диагностики** — `DiagnosticLogger` пишет и TXT, и JSON
   при каждом запуске. Имена файлов включают временну́ю метку для версионирования.

5. **Остатки — единственное персистентное состояние** — `RemnantDatabase` —
   единственный класс, имеющий право читать/писать `C:\DB_INS\remnants.json`.
   Прямой доступ к файлу из других классов запрещён.

## Code Examples

### Правильно: доменная модель без AutoCAD зависимостей

```csharp
// Models.cs — Domain Layer
// Только BCL типы, никаких Autodesk.* или NuGet-библиотек
public record Remnant(
    string Id,           // формат X.Y.NNN.ZZ
    double Width,
    double Height,
    string ParentId,
    PieceFate Fate);

public enum PieceFate { Active, UsedFull, UsedPartial, Discarded }
```

### Правильно: Application orchestration без AutoCAD

```csharp
// RollingHorizonEngine.cs — Application Layer
// НЕТ using Autodesk.*
public class RollingHorizonEngine
{
    private readonly IOrToolsOptimizer _optimizer;
    private readonly RemnantManager _remnantManager;

    public LayoutResult Execute(FacadeInput input, OptimizerConfig config)
    {
        var rows = _preprocessor.Divide(input);
        // ...пайплайн без AutoCAD API
    }
}
```

### Правильно: AutoCAD API только в Presentation

```csharp
// PluginCommands.cs — Presentation Layer
[CommandMethod("INS")]
public void InsCommand()
{
    var doc = Application.DocumentManager.MdiActiveDocument; // ← AutoCAD здесь
    var facade = ExtractFacadeFromDrawing(doc);              // ← AutoCAD здесь
    
    // Передаём чистую модель в Application слой
    var engine = new RollingHorizonEngine(...);
    var result = engine.Execute(facade, config);             // ← нет AutoCAD
    
    _visualizer.Render(doc, result);                         // ← AutoCAD здесь
}
```

### Правильно: Infrastructure реализует персистентность

```csharp
// RemnantDatabase.cs — Infrastructure Layer
// Единственный класс с доступом к C:\DB_INS\remnants.json
public class RemnantDatabase
{
    private const string DbPath = @"C:\DB_INS\remnants.json";

    public void Save(IEnumerable<Remnant> remnants)
    {
        var json = JsonConvert.SerializeObject(remnants, Formatting.Indented);
        File.WriteAllText(DbPath, json);
    }

    public List<Remnant> Load() { /* ... */ }
}
```

## Anti-Patterns

- ❌ Передавать `ObjectId` или `Entity` AutoCAD в `RollingHorizonEngine`,
  `OrToolsOptimizer` или любой Application/Domain класс
- ❌ Напрямую читать/писать `C:\DB_INS\remnants.json` из классов кроме `RemnantDatabase`
- ❌ Вызывать `DiagnosticLogger` из доменных моделей (`Models.cs`, `OrToolsModels.cs`)
- ❌ Добавлять бизнес-логику раскладки в `PluginCommands` — команды только
  извлекают данные из AutoCAD и передают в Application Layer
- ❌ Пропускать стадии пайплайна или менять их порядок без обновления
  `RollingHorizonEngine`
- ❌ Хранить остатки в памяти между сессиями AutoCAD — только через `RemnantDatabase`

## Headless Testing

`DxfHeadlessRunner` (tools/DxfHeadlessRunner/) демонстрирует правильное следование
архитектуре: весь Application и Domain слой запускается без AutoCAD. Если новый код
нарушает этот принцип — тест-раннер не скомпилируется.

```
tools/DxfHeadlessRunner/Program.cs
    ↓ использует только Application + Domain + Infrastructure
    ↓ НЕ ссылается на Autodesk.*
    ↓ читает .dxf через netDxf, строит FacadeInput вручную
```
