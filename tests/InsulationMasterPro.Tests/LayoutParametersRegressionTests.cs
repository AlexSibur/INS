using InsulationMasterPro.Models;
using InsulationMasterPro.OrTools;
using Xunit;

namespace InsulationMasterPro.Tests;

/// <summary>
/// Регрессионные тесты для критических дефолтов конфигурации.
/// Предотвращают повторение бага 2026-03-29: RollingHorizonRows=3 (legacy значение)
/// вызывало 38-минутное выполнение INS после реализации sliding window.
/// </summary>
public class LayoutParametersRegressionTests
{
    /// <summary>
    /// RollingHorizonRows=3 был legacy дефолт, который ДО 29.03.26 игнорировался.
    /// После реализации sliding window он стал реально использоваться → ~38 мин/фасад.
    /// Безопасный дефолт = 1 (каждый ряд независимо).
    /// </summary>
    [Fact]
    public void LayoutParameters_RollingHorizonRows_DefaultsToOne()
    {
        var lp = new LayoutParameters();
        Assert.Equal(1, lp.RollingHorizonRows);
    }

    [Fact]
    public void OptimizerConfig_RollingHorizonRows_DefaultsToOne()
    {
        var cfg = new OptimizerConfig();
        Assert.Equal(1, cfg.RollingHorizonRows);
    }

    /// <summary>
    /// При HorizonRows=1 и 5с таймауте адаптивный таймаут = max(15, 5*3) = 15c.
    /// При 19 рядах × 2 прохода = max ~570c (9.5 мин). Приемлемо.
    /// При HorizonRows=3 — та же математика, но модель в 3x сложнее → solver часто
    /// тратит весь таймаут → 19 × 2 × 4 попытки × 15c = ~2280c (38 мин). Недопустимо.
    /// </summary>
    [Fact]
    public void LayoutParameters_RollingHorizonTimeoutPerWindowSeconds_DefaultsFive()
    {
        var lp = new LayoutParameters();
        Assert.Equal(5, lp.RollingHorizonTimeoutPerWindowSeconds);
    }

    [Fact]
    public void OptimizerConfig_RollingHorizonTimeoutPerWindowSeconds_DefaultsFive()
    {
        var cfg = new OptimizerConfig();
        Assert.Equal(5, cfg.RollingHorizonTimeoutPerWindowSeconds);
    }
}
