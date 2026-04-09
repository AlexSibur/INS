# Project: InsulationMasterPro

## Overview
AutoCAD plugin for automated thermal insulation board layout on 2D facades with remnant optimization using OR-Tools CP-SAT solver and Clipper2 boolean geometry.

## Core Features
- Automatic layout of 1200×600 mm boards with ≥100 mm joint offset
- OR-Tools optimization — CP-SAT solver with Rolling Horizon, multi-pass and look-ahead
- Remnant management — lineage marking (X.Y.NNN.ZZ format), sub-cutting, large-piece priority, PieceFate tracking
- Intra-facade reuse — remnants from under windows reused in adjacent segments and subsequent rows
- Adaptive row heights — any height ≥150 mm, soft-penalty optimization
- Contour overhangs — 170 mm, alternating rows, trimming to windows
- L-boot elements — Boolean Subtraction at window openings
- Excel reporting — 3 sheets: Statistics, Remnant Movement, Summary
- Full diagnostics — TXT + JSON on each run

## Tech Stack
- **Language:** C# 12 / .NET 8.0 Windows
- **Framework:** AutoCAD .NET API (2025+)
- **Database:** JSON file-based (C:\DB_INS\)
- **ORM:** N/A
- **Key Libraries:** Google OR-Tools 9.10, Clipper2 1.3.0, ClosedXML 0.105.0, Newtonsoft.Json 13.0.3, AutoCAD.NET 25.1.0

## Architecture Notes
- AutoCAD plugin (not web application)
- Headless testing via DxfHeadlessRunner
- Pipeline: Preprocessor → PatternGenerator → RollingHorizonEngine → OrToolsOptimizer → Postprocessor → VisualizationEngine → DiagnosticLogger
- Remnant database persisted to C:\DB_INS\remnants.json

## Architecture
See `.ai-factory/ARCHITECTURE.md` for detailed architecture guidelines.
Pattern: Clean Architecture (AutoCAD Plugin)

## Non-Functional Requirements
- Logging: DiagnosticLogger outputs to C:\DB_INS\diagnostics.txt and diagnostics.json
- Error handling: Structured error responses with validation (C1-C17a)
- Platform: Windows-only (AutoCAD 2025+)
