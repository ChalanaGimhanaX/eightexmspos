from fastapi import APIRouter
from fastapi.responses import HTMLResponse

router = APIRouter(tags=["Store Manager & Owner Dashboard"])

DASHBOARD_HTML = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Enightx POS - Store Manager &amp; Owner Dashboard</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&display=swap" rel="stylesheet">
    <style>
        :root {
            --primary: #1E3A8A;
            --primary-light: #3B82F6;
            --accent: #10B981;
            --danger: #EF4444;
            --warning: #F59E0B;
            --warning-bg: #FEF3C7;
            --warning-border: #FCD34D;
            --bg-dark: #0F172A;
            --card-dark: #1E293B;
            --card-border: #334155;
            --text-main: #F8FAFC;
            --text-muted: #94A3B8;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: 'Inter', sans-serif;
        }

        body {
            background-color: var(--bg-dark);
            color: var(--text-main);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }

        header {
            background-color: var(--card-dark);
            border-bottom: 1px solid var(--card-border);
            padding: 1rem 2rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 1rem;
        }

        .brand-section {
            display: flex;
            align-items: center;
            gap: 1rem;
        }

        .logo-badge {
            background: linear-gradient(135deg, #2563EB, #1D4ED8);
            color: white;
            font-weight: 800;
            font-size: 1.25rem;
            padding: 0.5rem 0.85rem;
            border-radius: 8px;
            letter-spacing: 0.05em;
        }

        .brand-title h1 {
            font-size: 1.25rem;
            font-weight: 700;
            color: #FFFFFF;
        }

        .brand-title p {
            font-size: 0.8rem;
            color: var(--text-muted);
        }

        .controls-section {
            display: flex;
            align-items: center;
            gap: 1rem;
            flex-wrap: wrap;
        }

        .filter-group {
            display: flex;
            align-items: center;
            gap: 0.5rem;
            background: #0F172A;
            padding: 0.4rem 0.8rem;
            border-radius: 6px;
            border: 1px solid var(--card-border);
        }

        .filter-group label {
            font-size: 0.75rem;
            color: var(--text-muted);
            text-transform: uppercase;
            font-weight: 600;
        }

        .filter-group select, .filter-group input {
            background: transparent;
            border: none;
            color: var(--text-main);
            font-size: 0.85rem;
            font-weight: 500;
            outline: none;
        }

        .btn-refresh {
            background-color: #2563EB;
            color: white;
            border: none;
            padding: 0.5rem 1rem;
            border-radius: 6px;
            font-weight: 600;
            font-size: 0.85rem;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 0.4rem;
            transition: background 0.2s;
        }

        .btn-refresh:hover {
            background-color: #1D4ED8;
        }

        main {
            flex: 1;
            padding: 2rem;
            max-width: 1400px;
            margin: 0 auto;
            width: 100%;
        }

        /* A17 Warning Banner */
        .stale-warning-banner {
            display: none;
            background-color: var(--warning-bg);
            border: 1px solid var(--warning-border);
            color: #92400E;
            padding: 1rem 1.25rem;
            border-radius: 8px;
            margin-bottom: 1.5rem;
            align-items: center;
            gap: 0.75rem;
            font-size: 0.9rem;
            box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1);
        }

        .stale-warning-banner.visible {
            display: flex;
        }

        .stale-warning-banner .icon {
            font-size: 1.5rem;
        }

        /* KPI Cards Grid */
        .kpi-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 1rem;
            margin-bottom: 2rem;
        }

        .kpi-card {
            background: var(--card-dark);
            border: 1px solid var(--card-border);
            border-radius: 8px;
            padding: 1.25rem;
            transition: transform 0.2s, box-shadow 0.2s;
        }

        .kpi-card:hover {
            transform: translateY(-2px);
            box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.3);
        }

        .kpi-label {
            font-size: 0.75rem;
            font-weight: 600;
            color: var(--text-muted);
            text-transform: uppercase;
            letter-spacing: 0.05em;
            margin-bottom: 0.5rem;
        }

        .kpi-value {
            font-size: 1.75rem;
            font-weight: 700;
            color: #FFFFFF;
            margin-bottom: 0.25rem;
        }

        .kpi-subtext {
            font-size: 0.8rem;
            color: var(--text-muted);
        }

        .kpi-value.accent { color: var(--accent); }
        .kpi-value.danger { color: var(--danger); }
        .kpi-value.primary { color: var(--primary-light); }

        /* Two-column layout */
        .content-grid {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 1.5rem;
            margin-bottom: 2rem;
        }

        @media (max-width: 960px) {
            .content-grid {
                grid-template-columns: 1fr;
            }
        }

        .section-card {
            background: var(--card-dark);
            border: 1px solid var(--card-border);
            border-radius: 8px;
            padding: 1.25rem;
            display: flex;
            flex-direction: column;
        }

        .section-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 1rem;
            border-bottom: 1px solid var(--card-border);
            padding-bottom: 0.75rem;
        }

        .section-title {
            font-size: 1rem;
            font-weight: 600;
            color: #FFFFFF;
            display: flex;
            align-items: center;
            gap: 0.5rem;
        }

        .badge-pill {
            font-size: 0.7rem;
            font-weight: 600;
            padding: 0.2rem 0.5rem;
            border-radius: 9999px;
            background: #334155;
            color: var(--text-muted);
        }

        /* Tables */
        table {
            width: 100%;
            border-collapse: collapse;
            font-size: 0.85rem;
        }

        th {
            text-align: left;
            padding: 0.6rem 0.75rem;
            color: var(--text-muted);
            font-weight: 600;
            border-bottom: 1px solid var(--card-border);
            text-transform: uppercase;
            font-size: 0.7rem;
            letter-spacing: 0.05em;
        }

        td {
            padding: 0.65rem 0.75rem;
            border-bottom: 1px solid #1E293B;
            color: var(--text-main);
        }

        tr:last-child td {
            border-bottom: none;
        }

        .status-badge {
            display: inline-flex;
            align-items: center;
            gap: 0.35rem;
            font-size: 0.75rem;
            font-weight: 600;
            padding: 0.2rem 0.6rem;
            border-radius: 9999px;
        }

        .status-badge.online {
            background: rgba(16, 185, 129, 0.2);
            color: #34D399;
        }

        .status-badge.stale {
            background: rgba(245, 158, 11, 0.2);
            color: #FBBF24;
        }

        .status-badge.offline {
            background: rgba(239, 68, 68, 0.2);
            color: #F87171;
        }

        .status-dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background-color: currentColor;
        }

        /* Tender bars */
        .tender-row {
            margin-bottom: 0.85rem;
        }

        .tender-header {
            display: flex;
            justify-content: space-between;
            font-size: 0.85rem;
            margin-bottom: 0.35rem;
        }

        .tender-bar-bg {
            width: 100%;
            height: 8px;
            background: #0F172A;
            border-radius: 4px;
            overflow: hidden;
        }

        .tender-bar-fill {
            height: 100%;
            background: var(--primary-light);
            border-radius: 4px;
        }

        footer {
            background: var(--card-dark);
            border-top: 1px solid var(--card-border);
            padding: 1rem 2rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.75rem;
            color: var(--text-muted);
            flex-wrap: wrap;
            gap: 0.5rem;
        }
    </style>
</head>
<body>
    <header>
        <div class="brand-section">
            <div class="logo-badge">EX</div>
            <div class="brand-title">
                <h1>Enightx POS &bull; Store Manager &amp; Owner Dashboard</h1>
                <p>Real-time Business Overview &amp; Branch Freshness Synchronization</p>
            </div>
        </div>
        <div class="controls-section">
            <div class="filter-group">
                <label for="tenantInput">Tenant:</label>
                <input type="text" id="tenantInput" value="TENANT_LK_01" style="width: 110px;">
            </div>
            <div class="filter-group">
                <label for="branchSelect">Branch:</label>
                <select id="branchSelect">
                    <option value="">All Branches</option>
                    <option value="B01" selected>Branch B01</option>
                </select>
            </div>
            <button class="btn-refresh" onclick="loadDashboard()">
                <span>🔄</span> Refresh (<span id="countdown">30</span>s)
            </button>
        </div>
    </header>

    <main>
        <!-- A17 Stale Data Alert Banner -->
        <div id="staleWarning" class="stale-warning-banner">
            <div class="icon">⚠️</div>
            <div style="flex: 1;">
                <strong>A17 Freshness Alert:</strong>
                <span id="staleWarningText">One or more branch counters are offline or delayed in syncing. Cloud totals may not reflect the latest offline sales until counters reconnect.</span>
            </div>
        </div>

        <!-- KPI Grid -->
        <div class="kpi-grid">
            <div class="kpi-card">
                <div class="kpi-label">Today's Gross Revenue</div>
                <div id="kpiRevenue" class="kpi-value primary">LKR 0.00</div>
                <div id="kpiOrders" class="kpi-subtext">0 Completed Orders</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Average Ticket Size</div>
                <div id="kpiAvgTicket" class="kpi-value">LKR 0.00</div>
                <div class="kpi-subtext">Per Transaction</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Outstanding Debt</div>
                <div id="kpiDebt" class="kpi-value danger">LKR 0.00</div>
                <div class="kpi-subtext">Across All Customers</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Low Stock Alerts (A06)</div>
                <div id="kpiLowStock" class="kpi-value accent">0 Items</div>
                <div class="kpi-subtext">Below Reorder Point</div>
            </div>
        </div>

        <!-- Main Content 2 Columns -->
        <div class="content-grid">
            <!-- Branch & Counter Freshness Monitor (A17) -->
            <div class="section-card">
                <div class="section-header">
                    <div class="section-title">
                        <span>📡</span> Branch &amp; Counter Freshness Monitor (A17)
                    </div>
                    <span id="deviceCountBadge" class="badge-pill">0 Devices</span>
                </div>
                <div style="overflow-x: auto;">
                    <table>
                        <thead>
                            <tr>
                                <th>Branch</th>
                                <th>Counter</th>
                                <th>Name</th>
                                <th>Version</th>
                                <th>Last Seen</th>
                                <th>Freshness</th>
                            </tr>
                        </thead>
                        <tbody id="freshnessTableBody">
                            <tr><td colspan="6" style="text-align: center; color: var(--text-muted);">Loading devices...</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>

            <!-- Tender Breakdown -->
            <div class="section-card">
                <div class="section-header">
                    <div class="section-title">
                        <span>💳</span> Tender Breakdown
                    </div>
                    <span class="badge-pill">Settlements</span>
                </div>
                <div id="tenderBreakdownContainer" style="padding-top: 0.5rem;">
                    <div class="tender-row">
                        <div class="tender-header">
                            <span>💵 Cash</span>
                            <span id="tenderCash">LKR 0.00 (0%)</span>
                        </div>
                        <div class="tender-bar-bg"><div id="barCash" class="tender-bar-fill" style="width: 0%; background: #10B981;"></div></div>
                    </div>
                    <div class="tender-row">
                        <div class="tender-header">
                            <span>💳 Card (POS Terminal)</span>
                            <span id="tenderCard">LKR 0.00 (0%)</span>
                        </div>
                        <div class="tender-bar-bg"><div id="barCard" class="tender-bar-fill" style="width: 0%; background: #3B82F6;"></div></div>
                    </div>
                    <div class="tender-row">
                        <div class="tender-header">
                            <span>📱 QR (LANKAQR)</span>
                            <span id="tenderQr">LKR 0.00 (0%)</span>
                        </div>
                        <div class="tender-bar-bg"><div id="barQr" class="tender-bar-fill" style="width: 0%; background: #8B5CF6;"></div></div>
                    </div>
                    <div class="tender-row">
                        <div class="tender-header">
                            <span>📝 Credit / Customer Debt</span>
                            <span id="tenderCredit">LKR 0.00 (0%)</span>
                        </div>
                        <div class="tender-bar-bg"><div id="barCredit" class="tender-bar-fill" style="width: 0%; background: #F59E0B;"></div></div>
                    </div>
                </div>
            </div>
        </div>

        <!-- Secondary Grid: Cashier Performance & Top Products -->
        <div class="content-grid">
            <!-- Cashier Performance & Shift Variance -->
            <div class="section-card">
                <div class="section-header">
                    <div class="section-title">
                        <span>👤</span> Cashier Performance &amp; Variances
                    </div>
                    <span class="badge-pill">Staff Audit</span>
                </div>
                <div style="overflow-x: auto;">
                    <table>
                        <thead>
                            <tr>
                                <th>Cashier ID</th>
                                <th>Shifts</th>
                                <th>Total Sales</th>
                                <th>Cash Variance</th>
                            </tr>
                        </thead>
                        <tbody id="cashierTableBody">
                            <tr><td colspan="4" style="text-align: center; color: var(--text-muted);">No sales data available.</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>

            <!-- Top Products -->
            <div class="section-card">
                <div class="section-header">
                    <div class="section-title">
                        <span>🏆</span> Top Selling Products
                    </div>
                    <span class="badge-pill">By Revenue</span>
                </div>
                <div style="overflow-x: auto;">
                    <table>
                        <thead>
                            <tr>
                                <th>Product Name</th>
                                <th>Qty Sold</th>
                                <th>Revenue</th>
                            </tr>
                        </thead>
                        <tbody id="topProductsTableBody">
                            <tr><td colspan="3" style="text-align: center; color: var(--text-muted);">No products sold yet.</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>
        </div>
    </main>

    <footer>
        <div>Enightx POS &bull; Hybrid Offline-First Architecture &bull; Specification v1.0</div>
        <div>Last Checked: <span id="lastCheckedText">Never</span> &bull; A17/A08/A09 Compliant</div>
    </footer>

    <script>
        let countdownSec = 30;

        function formatLkr(val) {
            const num = parseFloat(val) || 0;
            return 'LKR ' + num.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        }

        function formatTimeAgo(isoString) {
            if (!isoString) return 'Never';
            const date = new Date(isoString);
            const now = new Date();
            const diffMs = now - date;
            const diffSec = Math.floor(diffMs / 1000);
            if (diffSec < 60) return `${diffSec}s ago`;
            const diffMin = Math.floor(diffSec / 60);
            if (diffMin < 60) return `${diffMin}m ago`;
            const diffHours = Math.floor(diffMin / 60);
            return `${diffHours}h ago`;
        }

        async function loadDashboard() {
            countdownSec = 30;
            const tenantId = document.getElementById('tenantInput').value.trim() || 'TENANT_LK_01';
            const branchId = document.getElementById('branchSelect').value;

            const branchQuery = branchId ? `&branch_id=${encodeURIComponent(branchId)}` : '';

            try {
                // 1. Fetch Dashboard Summary
                const dashRes = await fetch(`/api/v1/reports/dashboard?tenant_id=${encodeURIComponent(tenantId)}`);
                if (dashRes.ok) {
                    const dash = await dashRes.json();
                    document.getElementById('kpiRevenue').innerText = formatLkr(dash.total_revenue);
                    document.getElementById('kpiOrders').innerText = `${dash.total_orders} Completed Orders`;
                    document.getElementById('kpiDebt').innerText = formatLkr(dash.total_customer_debt);
                    document.getElementById('kpiLowStock').innerText = `${dash.low_stock_alerts} Items`;
                    
                    const avg = dash.total_orders > 0 ? (parseFloat(dash.total_revenue) / dash.total_orders) : 0;
                    document.getElementById('kpiAvgTicket').innerText = formatLkr(avg);

                    // Render Tenders
                    const tenders = dash.tender_breakdown || {};
                    const cash = parseFloat(tenders.CASH || 0);
                    const card = parseFloat(tenders.CARD || 0);
                    const qr = parseFloat(tenders.QR || 0);
                    const credit = parseFloat(tenders.CREDIT || 0);
                    const totalTender = (cash + card + qr + credit) || 1;

                    const pCash = Math.round((cash / totalTender) * 100);
                    const pCard = Math.round((card / totalTender) * 100);
                    const pQr = Math.round((qr / totalTender) * 100);
                    const pCredit = Math.round((credit / totalTender) * 100);

                    document.getElementById('tenderCash').innerText = `${formatLkr(cash)} (${pCash}%)`;
                    document.getElementById('barCash').style.width = `${pCash}%`;

                    document.getElementById('tenderCard').innerText = `${formatLkr(card)} (${pCard}%)`;
                    document.getElementById('barCard').style.width = `${pCard}%`;

                    document.getElementById('tenderQr').innerText = `${formatLkr(qr)} (${pQr}%)`;
                    document.getElementById('barQr').style.width = `${pQr}%`;

                    document.getElementById('tenderCredit').innerText = `${formatLkr(credit)} (${pCredit}%)`;
                    document.getElementById('barCredit').style.width = `${pCredit}%`;
                }

                // 2. Fetch Branch Freshness (A17)
                const freshRes = await fetch(`/api/v1/reports/branch-freshness?tenant_id=${encodeURIComponent(tenantId)}${branchQuery}`);
                if (freshRes.ok) {
                    const freshData = await freshRes.json();
                    const banner = document.getElementById('staleWarning');
                    if (freshData.has_stale_counters) {
                        banner.classList.add('visible');
                        document.getElementById('staleWarningText').innerText = freshData.warning_message || 'Stale counter data detected.';
                    } else {
                        banner.classList.remove('visible');
                    }

                    const tbody = document.getElementById('freshnessTableBody');
                    document.getElementById('deviceCountBadge').innerText = `${freshData.devices.length} Devices`;

                    if (freshData.devices.length === 0) {
                        tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; color: var(--text-muted);">No enrolled counters registered.</td></tr>';
                    } else {
                        tbody.innerHTML = freshData.devices.map(d => {
                            const badgeClass = d.status === 'ONLINE' ? 'online' : (d.status === 'STALE' ? 'stale' : 'offline');
                            return `
                                <tr>
                                    <td><strong>${d.branch_id}</strong></td>
                                    <td>${d.device_code}</td>
                                    <td>${d.device_name}</td>
                                    <td><span style="font-family: monospace; font-size: 0.8rem;">${d.app_version}</span></td>
                                    <td>${formatTimeAgo(d.last_seen_at)}</td>
                                    <td>
                                        <span class="status-badge ${badgeClass}">
                                            <span class="status-dot"></span> ${d.status}
                                        </span>
                                    </td>
                                </tr>
                            `;
                        }).join('');
                    }
                }

                // 3. Fetch Top Products
                const topRes = await fetch(`/api/v1/reports/top-products?tenant_id=${encodeURIComponent(tenantId)}${branchQuery}&limit=5`);
                if (topRes.ok) {
                    const topItems = await topRes.json();
                    const topBody = document.getElementById('topProductsTableBody');
                    if (topItems.length === 0) {
                        topBody.innerHTML = '<tr><td colspan="3" style="text-align: center; color: var(--text-muted);">No products sold yet.</td></tr>';
                    } else {
                        topBody.innerHTML = topItems.map(p => `
                            <tr>
                                <td><strong>${p.product_name}</strong></td>
                                <td>${parseFloat(p.quantity_sold).toFixed(0)}</td>
                                <td>${formatLkr(p.revenue)}</td>
                            </tr>
                        `).join('');
                    }
                }

                // 4. Fetch Cashier Performance
                const cashierRes = await fetch(`/api/v1/reports/cashier-performance?tenant_id=${encodeURIComponent(tenantId)}${branchQuery}`);
                if (cashierRes.ok) {
                    const cashiers = await cashierRes.json();
                    const cBody = document.getElementById('cashierTableBody');
                    if (cashiers.length === 0) {
                        cBody.innerHTML = '<tr><td colspan="4" style="text-align: center; color: var(--text-muted);">No cashier sales recorded.</td></tr>';
                    } else {
                        cBody.innerHTML = cashiers.map(c => `
                            <tr>
                                <td><strong>${c.cashier_id}</strong></td>
                                <td>${c.shifts_worked}</td>
                                <td>${formatLkr(c.total_sales_amount)}</td>
                                <td style="color: ${parseFloat(c.total_variance) < 0 ? 'var(--danger)' : 'var(--text-main)'}">
                                    ${formatLkr(c.total_variance)}
                                </td>
                            </tr>
                        `).join('');
                    }
                }

                document.getElementById('lastCheckedText').innerText = new Date().toLocaleTimeString();
            } catch (err) {
                console.error('Error loading dashboard:', err);
            }
        }

        // Timer for auto-refresh
        setInterval(() => {
            countdownSec--;
            document.getElementById('countdown').innerText = countdownSec;
            if (countdownSec <= 0) {
                loadDashboard();
            }
        }, 1000);

        document.addEventListener('DOMContentLoaded', () => {
            loadDashboard();
        });
    </script>
</body>
</html>
"""

@router.get("/dashboard", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
def get_dashboard_html():
    return HTMLResponse(content=DASHBOARD_HTML, status_code=200)

