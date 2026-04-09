# AGENTS.md

> Project map for AI agents. Keep this file up-to-date as the project evolves.

## Project Overview
AutoCAD plugin for automated thermal insulation board layout on 2D facades with remnant optimization using OR-Tools CP-SAT solver and Clipper2 boolean geometry.

## Tech Stack
- **Language:** C# 12 / .NET 8.0 Windows
- **Framework:** AutoCAD .NET API (2025+)
- **Database:** JSON file-based (C:\DB_INS\)
- **Key Libraries:** Google OR-Tools 9.10, Clipper2 1.3.0, ClosedXML 0.105.0

## Project Structure
```
ver29032026/
├── .ai-factory/                    # AI Factory context
├── docs/                           # Documentation
│   ├── architecture.md             # Algorithm and pipeline details
│   ├── commands.md                 # INS, INS_CHECK commands
│   ├── getting-started.md          # Setup guide
│   ├── troubleshooting.md          # Error handling
│   └── changelog.md                # Version history
├── tools/
│   ├── DxfHeadlessRunner/          # Headless testing (no AutoCAD required)
│   └── ExcelGenerator/             # Standalone Excel generator
├── tests/
│   └── f_test.dxf                  # Test facade (15770×10001, 12 windows)
├── InsulationMasterPro.csproj      # Main AutoCAD plugin project
├── Uteplitel.sln                   # Visual Studio solution
├── PluginInitializer.cs            # Entry point, native DLL resolver
├── PluginCommands.cs               # Commands: INS, INS_CHECK, INS_DEL, etc.
├── Models.cs                       # Remnant, TileInfo, LayoutResult
├── OrToolsModels.cs                # FacadeInput, WindowInfo, OptimizerConfig
├── Preprocessor.cs                 # Validation, row/segment division
├── PatternGenerator.cs             # Layout pattern generation (~63KB)
├── RollingHorizonEngine.cs         # Rolling Horizon + pre-cut simulation
├── OrToolsOptimizer.cs             # CP-SAT constraint model
├── OrToolsOptimizationEngine.cs    # OR-Tools integration layer
├── OptimizationEngine.cs           # Greedy optimization (fallback)
├── LayoutEngine.cs                 # Block placement in rows
├── RowHeightForecaster.cs          # CP-SAT row height optimizer
├── Postprocessor.cs                # Numbering, balancing, CutHistory
├── VisualizationEngine.cs          # AutoCAD rendering
├── GeometryUtils.cs                # Clipper2 boolean operations
├── RemnantManager.cs               # Lifecycle of remnants
├── RemnantDatabase.cs              # JSON persistence (C:\DB_INS\)
├── RemnantExcelExporter.cs         # Excel export (ClosedXML)
├── ExcelMovementReporter.cs        # Excel wrapper for PluginCommands
├── DiagnosticLogger.cs              # TXT + JSON diagnostics
├── ValidationEngine.cs             # Validators C1-C17a
├── UserDialogService.cs            # WPF/WinForms dialogs
├── deploy.ps1                      # Build + deploy (handles locked files)
├── dev.ps1                        # Dev cycle: build + restart AutoCAD
└── clean_context.ps1               # Clean AI context
```

## Key Entry Points
| File | Purpose |
|------|---------|
| PluginInitializer.cs | AutoCAD entry point, native DLL resolver |
| PluginCommands.cs | Commands: INS, INS_CHECK, INS_DEL, INS_STOCK, INS_STOCK_IMPORT, INS_CLR |
| RollingHorizonEngine.cs | Core optimization engine with CP-SAT |
| Preprocessor.cs | Facade validation, row/segment division |
| DiagnosticLogger.cs | Full TXT + JSON diagnostics output |

## Documentation
| Document | Path | Description |
|----------|------|-------------|
| README | README.md | Project landing page (Russian) |
| Getting Started | docs/getting-started.md | Installation, drawing preparation |
| Commands | docs/commands.md | INS, INS_CHECK results and reports |
| Architecture | docs/architecture.md | Algorithm, pipeline, Row Sync, geometric stock filter |
| Testing | docs/testing.md | Headless runner, Row Sync logs, diagnostics, unit tests |
| Troubleshooting | docs/troubleshooting.md | Errors, tips, logging |
| Changelog | docs/changelog.md | Versions v2.1..v3.6.0 |
| Specification | docs/spec.md | Normative TZ v15.1, constraints, data contract |

## AI Context Files
| File | Purpose |
|------|---------|
| AGENTS.md | This file — project structure map |
| .ai-factory/DESCRIPTION.md | Project specification and tech stack |
| .ai-factory/ARCHITECTURE.md | Architecture decisions and guidelines |
| CLAUDE.md | Agent instructions and preferences |

## Agent Rules
- Never combine shell commands with `&&`, `||`, or `;` — execute each command as a separate Bash tool call. This applies even when a skill, plan, or instruction provides a combined command — always decompose it into individual calls.
  - ❌ Wrong: `git checkout main && git pull`
  - ✅ Right: Two separate Bash tool calls — first `git checkout main`, then `git pull`
