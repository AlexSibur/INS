[Back to README](../README.md) · [Команды →](commands.md)

# Начало работы

## Системные требования

- **AutoCAD:** 2025 и новее
- **.NET:** 8.0 Windows (поставляется с AutoCAD)
- **Библиотеки:** Clipper2, Newtonsoft.Json, ClosedXML (включены в сборку)

## Установка

### Сборка

```bash
dotnet build InsulationMasterPro.csproj -c Release -p:Platform=x64
```

### Деплой в AutoCAD

```powershell
.\deploy.ps1
```

Скрипт копирует все необходимые DLL в папку плагинов AutoCAD. При заблокированных файлах — автоматически переименовывает старые в `.bak`.

### Цикл разработки

```powershell
.\dev.ps1 -Drawing "tests\f_test.dxf"
```

Сборка + закрытие AutoCAD + деплой + запуск с чертежом. Подробнее: [Тестирование](testing.md).

### Ручная установка

Из `deploy\` скопируйте все файлы в одну папку (например, `C:\AutoCAD_Plugins\InsulationMasterPro\`):

| Файл | Назначение |
|------|-----------|
| `InsulationMasterPro.dll` | Основная сборка |
| `InsulationMasterPro.pdb` | Отладочные символы |
| `InsulationMasterPro.deps.json` | Зависимости .NET |
| `Clipper2Lib.dll` | Геометрия (boolean ops) |
| `Google.OrTools.dll` | OR-Tools managed |
| `Google.Protobuf.dll` | Protobuf для OR-Tools |
| `google-ortools-native.dll` | OR-Tools native (~25 MB) |
| `Newtonsoft.Json.dll` | Сериализация JSON |
| `ClosedXML.dll` | Excel-отчёты |
| `ClosedXML.Parser.dll` | Парсер для ClosedXML |
| `DocumentFormat.OpenXml.dll` | OpenXml для Excel |
| `DocumentFormat.OpenXml.Framework.dll` | OpenXml framework |
| `ExcelNumberFormat.dll` | Форматирование чисел |
| `RBush.dll` | R-tree индекс |
| `SixLabors.Fonts.dll` | Шрифты |
| `System.IO.Packaging.dll` | Работа с пакетами |
| `ExcelGenerator.exe` | Fallback-генератор Excel (если ClosedXML не загружается в AutoCAD) |

> [!TIP]
> Рекомендуется использовать `.\deploy.ps1` — скрипт автоматически собирает и копирует все необходимые файлы.

> [!NOTE]
> Сборки AutoCAD (AcCoreMgd.dll, AcDbMgd.dll) копировать **не нужно** — они есть в AutoCAD.

### Загрузка плагина

**Через NETLOAD (первая проверка):**
1. Запустите AutoCAD 2025+
2. Команда → `NETLOAD`
3. Выберите `InsulationMasterPro.dll`
4. Доступные команды: `INS`, `INS_CHECK`, `INS_DEL`, `INS_STOCK`, `INS_STOCK_IMPORT`, `INS_CLR`

**Автозагрузка:**
1. **Файл** → **Параметры** → вкладка **Файлы**
2. **Путь поддержки поиска** → **Добавить** → папка с DLL
3. Или через **APPLOAD** → добавить в список автозагрузки

## Подготовка чертежа

### Слои

| Слой | Содержимое |
|------|-----------|
| `Внешний контур фасада` | Замкнутые полилинии — границы фасадов |
| `Контуры окон` | Замкнутые полилинии — оконные/дверные проёмы |

### Требования к геометрии

- Все контуры — **замкнутые** полилинии (LWPOLYLINE)
- Без **самопересечений**
- Окна **внутри** контуров фасадов
- Минимальная площадь контура: 0.1 м²

### Проверка

```
INS_CHECK
```

Команда проверяет замкнутость, самопересечения и расположение контуров.

## Быстрый старт

1. Создайте слои и нарисуйте контуры фасада и окон
2. `NETLOAD` → загрузите `InsulationMasterPro.dll`
3. `INS` → раскладка начнётся автоматически

## See Also

- [Команды плагина](commands.md) — INS, INS_CHECK, INS_DEL и другие
- [Архитектура и алгоритм](architecture.md) — как работает раскладка
