namespace Enightx.Pos.Common;

public static class MoneyCalculator
{
    public static decimal Round(decimal value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    public record LineCalculationResult(
        decimal Subtotal,
        decimal DiscountAmount,
        decimal TaxAmount,
        decimal LineTotal
    );

    public static LineCalculationResult CalculateLine(
        decimal quantity,
        decimal unitPrice,
        decimal discountRate = 0.0m,
        decimal discountFixed = 0.0m,
        decimal taxRate = 0.0m)
    {
        var subtotal = Round(quantity * unitPrice);
        var discountVal = (subtotal * discountRate) + discountFixed;
        var discountAmount = Round(discountVal);
        if (discountAmount > subtotal)
        {
            discountAmount = subtotal;
        }

        var netAfterDiscount = subtotal - discountAmount;
        var taxAmount = Round(netAfterDiscount * taxRate);
        var lineTotal = netAfterDiscount + taxAmount;

        return new LineCalculationResult(subtotal, discountAmount, taxAmount, lineTotal);
    }

    public static decimal CalculateShiftExpectedCash(
        decimal openingFloat,
        decimal cashReceived,
        decimal changeGiven,
        decimal cashRefunds,
        decimal cashIn,
        decimal cashOut)
    {
        return Round(openingFloat + cashReceived - changeGiven - cashRefunds + cashIn - cashOut);
    }

    public static decimal CalculateMovingWeightedAverageCost(
        decimal currentStock,
        decimal currentCostBasis,
        decimal receivedQty,
        decimal unitCost)
    {
        var newTotalQty = currentStock + receivedQty;
        if (newTotalQty <= 0)
        {
            return Round(unitCost);
        }
        if (currentStock <= 0)
        {
            return Round(unitCost);
        }
        var totalValue = (currentStock * currentCostBasis) + (receivedQty * unitCost);
        return Round(totalValue / newTotalQty);
    }
}
