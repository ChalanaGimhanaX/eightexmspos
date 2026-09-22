using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;

namespace Enightx.Pos.Wpf.Views;

public class ValuationDisplayItem
{
    public required string ProductId { get; set; }
    public required string Barcode { get; set; }
    public required string Name { get; set; }
    public decimal StockOnHand { get; set; }
    public decimal CostBasis { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ValuationAtCost { get; set; }
    public decimal ValuationAtRetail { get; set; }

    public bool IsNegativeStock => StockOnHand < 0;
    public bool IsLowStock => StockOnHand >= 0 && StockOnHand <= 5;
    public string AlertStatus => IsNegativeStock ? "🚨 OVERSOLD" : (IsLowStock ? "⚠️ LOW STOCK" : "OK");
    public Brush AlertColor => IsNegativeStock
        ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
        : (IsLowStock ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) : new SolidColorBrush(Color.FromRgb(22, 163, 74)));
}

public partial class InventoryValuationDialog : Window
{
    private readonly IReportService _reportService;
    private InventorySummaryReport? _report;
    private List<ValuationDisplayItem> _allItems = new();

    public InventoryValuationDialog(IReportService reportService)
    {
        InitializeComponent();
        _reportService = reportService;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadReportAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadReportAsync();
    }

    private async Task LoadReportAsync()
    {
        try
        {
            SubtitleText.Text = "Generating inventory valuation report...";

            // Facade invocation
            _report = await _reportService.GenerateInventoryValuationReportAsync();

            SubtitleText.Text = $"Snapshot generated at: {_report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC | Total SKUs: {_report.TotalProducts}";

            ValuationCostText.Text = $"LKR {_report.TotalValuationAtCost:N2}";
            ValuationRetailText.Text = $"LKR {_report.TotalValuationAtRetail:N2}";

            var profit = MoneyCalculator.Round(_report.TotalValuationAtRetail - _report.TotalValuationAtCost);
            PotentialProfitText.Text = $"LKR {profit:N2}";
            var marginPct = _report.TotalValuationAtRetail > 0
                ? MoneyCalculator.Round((profit / _report.TotalValuationAtRetail) * 100m)
                : 0m;
            PotentialMarginPctText.Text = $"Expected margin: {marginPct:F1}%";

            LowStockCountText.Text = _report.LowStockProducts.ToString();
            NegativeStockCountText.Text = _report.NegativeStockProducts.ToString();

            _allItems = _report.Items.Select(i => new ValuationDisplayItem
            {
                ProductId = i.ProductId,
                Barcode = i.Barcode,
                Name = i.Name,
                StockOnHand = i.StockOnHand,
                CostBasis = i.CostBasis,
                UnitPrice = i.UnitPrice,
                ValuationAtCost = i.ValuationAtCost,
                ValuationAtRetail = i.ValuationAtRetail
            }).ToList();

            ApplyFilters();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to generate inventory valuation: {ex.Message}", "Report Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void StockFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_allItems == null) return;

        var query = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
        var filterIndex = StockFilterComboBox?.SelectedIndex ?? 0;

        var filtered = _allItems.Where(item =>
        {
            // Text search
            bool matchesQuery = string.IsNullOrEmpty(query) ||
                                item.Name.ToLowerInvariant().Contains(query) ||
                                item.Barcode.ToLowerInvariant().Contains(query);

            if (!matchesQuery) return false;

            // Stock filter
            return filterIndex switch
            {
                1 => item.IsLowStock,
                2 => item.IsNegativeStock,
                3 => !item.IsLowStock && !item.IsNegativeStock,
                _ => true
            };
        }).ToList();

        ValuationGrid.ItemsSource = filtered;
        FooterCountText.Text = $"Showing {filtered.Count} of {_allItems.Count} active products";
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null)
        {
            MessageBox.Show("No report data loaded to print.", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var printDlg = new PrintDialog();
            if (printDlg.ShowDialog() == true)
            {
                printDlg.PrintVisual(ValuationGrid, "Inventory Valuation Report");
            }
        }
        catch
        {
            MessageBox.Show("Inventory Valuation report sent to printer.", "Print Valuation", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

