using System.Text.Json;
using Xunit;
using Enightx.Pos.Common;

namespace Enightx.Pos.Tests;

public class A22_ArithmeticFixtureTests
{
    private class FixtureLineTest
    {
        public string name { get; set; } = "";
        public decimal quantity { get; set; }
        public decimal unit_price { get; set; }
        public decimal discount_rate { get; set; }
        public decimal discount_fixed { get; set; }
        public decimal tax_rate { get; set; }
        public decimal expected_line_subtotal { get; set; }
        public decimal expected_discount_amount { get; set; }
        public decimal expected_tax_amount { get; set; }
        public decimal expected_line_total { get; set; }
    }

    private class FixtureRoot
    {
        public string version { get; set; } = "";
        public string currency { get; set; } = "";
        public List<FixtureLineTest> line_tests { get; set; } = new();
    }

    [Fact]
    public void A22_SharedArithmeticFixtures_MatchExactRoundingRules()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/fixtures/arithmetic_fixtures.json");
        Assert.True(File.Exists(fixturePath), $"Fixture file not found at {fixturePath}");

        var json = File.ReadAllText(fixturePath);
        var data = JsonSerializer.Deserialize<FixtureRoot>(json);
        Assert.NotNull(data);
        Assert.Equal("LKR", data.currency);

        foreach (var testCase in data.line_tests)
        {
            var result = MoneyCalculator.CalculateLine(
                testCase.quantity,
                testCase.unit_price,
                testCase.discount_rate,
                testCase.discount_fixed,
                testCase.tax_rate
            );

            Assert.True(
                testCase.expected_line_subtotal == result.Subtotal,
                $"Subtotal mismatch in '{testCase.name}': expected {testCase.expected_line_subtotal}, got {result.Subtotal}"
            );

            Assert.True(
                testCase.expected_discount_amount == result.DiscountAmount,
                $"Discount mismatch in '{testCase.name}': expected {testCase.expected_discount_amount}, got {result.DiscountAmount}"
            );

            Assert.True(
                testCase.expected_tax_amount == result.TaxAmount,
                $"Tax mismatch in '{testCase.name}': expected {testCase.expected_tax_amount}, got {result.TaxAmount}"
            );

            Assert.True(
                testCase.expected_line_total == result.LineTotal,
                $"Line total mismatch in '{testCase.name}': expected {testCase.expected_line_total}, got {result.LineTotal}"
            );
        }
    }
}
