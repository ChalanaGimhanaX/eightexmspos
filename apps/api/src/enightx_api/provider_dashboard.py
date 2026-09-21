from fastapi import APIRouter
from fastapi.responses import HTMLResponse

router = APIRouter(tags=["Provider Web Dashboard"])

PROVIDER_HTML = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Enightx POS - Provider Central Management Portal</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700;800&display=swap" rel="stylesheet">
    <style>
        :root {
            --primary: #4F46E5;
            --primary-hover: #4338CA;
            --accent: #10B981;
            --danger: #EF4444;
            --warning: #F59E0B;
            --bg-dark: #0B0F19;
            --card-dark: #151C2C;
            --card-border: #232F47;
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
            padding: 1.25rem 2rem;
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
            background: linear-gradient(135deg, #4F46E5, #3730A3);
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
        }

        .btn-refresh {
            background-color: var(--primary);
            color: white;
            border: none;
            padding: 0.5rem 1.25rem;
            border-radius: 6px;
            font-weight: 600;
            font-size: 0.85rem;
            cursor: pointer;
            transition: background 0.2s;
        }
        .btn-refresh:hover { background-color: var(--primary-hover); }

        main {
            flex: 1;
            padding: 2rem;
            max-width: 1440px;
            margin: 0 auto;
            width: 100%;
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

        .kpi-value.accent { color: var(--accent); }
        .kpi-value.danger { color: var(--danger); }
        .kpi-value.primary { color: #818CF8; }

        /* Two-column layout */
        .content-grid {
            display: grid;
            grid-template-columns: 2fr 1fr;
            gap: 1.5rem;
            margin-bottom: 2rem;
        }

        @media (max-width: 1024px) {
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
            margin-bottom: 1.5rem;
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
        }

        table {
            width: 100%;
            border-collapse: collapse;
            font-size: 0.85rem;
        }

        th, td {
            padding: 0.75rem 0.5rem;
            text-align: left;
            border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        }

        th {
            color: var(--text-muted);
            font-weight: 600;
            font-size: 0.75rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
        }

        tbody tr:hover {
            background-color: rgba(255, 255, 255, 0.02);
        }

        .status-badge {
            display: inline-block;
            padding: 0.25rem 0.6rem;
            border-radius: 9999px;
            font-size: 0.75rem;
            font-weight: 600;
        }

        .status-badge.online { background-color: rgba(16, 185, 129, 0.15); color: #34D399; }
        .status-badge.offline { background-color: rgba(239, 68, 68, 0.15); color: #F87171; }
        .status-badge.frozen { background-color: rgba(245, 158, 11, 0.15); color: #FBBF24; }

        .btn-action {
            padding: 0.35rem 0.7rem;
            border-radius: 4px;
            border: none;
            font-size: 0.75rem;
            font-weight: 600;
            cursor: pointer;
            transition: opacity 0.2s;
        }
        .btn-action:hover { opacity: 0.85; }
        .btn-action.danger { background: #DC2626; color: white; }
        .btn-action.success { background: #059669; color: white; }

        /* Forms */
        .form-group {
            margin-bottom: 1rem;
        }
        .form-group label {
            display: block;
            font-size: 0.8rem;
            color: var(--text-muted);
            margin-bottom: 0.35rem;
            font-weight: 500;
        }
        .form-group input, .form-group select {
            width: 100%;
            background: #0B0F19;
            border: 1px solid var(--card-border);
            color: white;
            padding: 0.5rem 0.75rem;
            border-radius: 6px;
            font-size: 0.85rem;
        }

        .btn-submit {
            width: 100%;
            background: var(--primary);
            color: white;
            border: none;
            padding: 0.65rem;
            border-radius: 6px;
            font-weight: 600;
            font-size: 0.85rem;
            cursor: pointer;
            margin-top: 0.5rem;
        }
        .btn-submit:hover { background: var(--primary-hover); }

        footer {
            text-align: center;
            padding: 1.5rem;
            font-size: 0.75rem;
            color: var(--text-muted);
            border-top: 1px solid var(--card-border);
        }
    </style>
</head>
<body>
    <header>
        <div class="brand-section">
            <div class="logo-badge">EX</div>
            <div class="brand-title">
                <h1>Enightx Cloud Provider Portal</h1>
                <p>Multi-Tenant Licensing, Payments &amp; Device Fleet Control</p>
            </div>
        </div>
        <div class="controls-section">
            <button class="btn-refresh" onclick="loadOverview()">🔄 Refresh Data</button>
        </div>
    </header>

    <main>
        <!-- KPI Overview -->
        <div class="kpi-grid">
            <div class="kpi-card">
                <div class="kpi-label">Registered Businesses</div>
                <div class="kpi-value primary" id="kpiBusinesses">-</div>
                <div class="kpi-subtext">Active shop tenants</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Total Counters</div>
                <div class="kpi-value" id="kpiDevices">-</div>
                <div class="kpi-subtext"><span id="kpiOnline" style="color: #34D399;">0</span> online | <span id="kpiOffline" style="color: #F87171;">0</span> offline</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Frozen Terminals</div>
                <div class="kpi-value danger" id="kpiFrozen">-</div>
                <div class="kpi-subtext">Remotely locked out</div>
            </div>
            <div class="kpi-card">
                <div class="kpi-label">Verified Revenue</div>
                <div class="kpi-value accent" id="kpiRevenue">LKR 0.00</div>
                <div class="kpi-subtext">Subscription collections</div>
            </div>
        </div>

        <div class="content-grid">
            <!-- Left Column: Device Fleet & Health -->
            <div>
                <div class="section-card">
                    <div class="section-header">
                        <div class="section-title">🖥 Device Fleet &amp; Health Telemetry</div>
                    </div>
                    <table>
                        <thead>
                            <tr>
                                <th>Device ID</th>
                                <th>Tenant</th>
                                <th>Branch</th>
                                <th>Version</th>
                                <th>Status</th>
                                <th>Last Seen</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody id="devicesTableBody">
                            <tr><td colspan="7" style="text-align: center; color: var(--text-muted);">Loading devices...</td></tr>
                        </tbody>
                    </table>
                </div>

                <div class="section-card">
                    <div class="section-header">
                        <div class="section-title">📜 Issued Entitlements &amp; Licenses</div>
                    </div>
                    <table>
                        <thead>
                            <tr>
                                <th>License ID</th>
                                <th>Tenant</th>
                                <th>Device</th>
                                <th>Plan</th>
                                <th>Issued</th>
                                <th>Expires</th>
                                <th>Status</th>
                            </tr>
                        </thead>
                        <tbody id="licensesTableBody">
                            <tr><td colspan="7" style="text-align: center; color: var(--text-muted);">Loading licenses...</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>

            <!-- Right Column: Administrative Actions -->
            <div>
                <!-- Issue License Card -->
                <div class="section-card">
                    <div class="section-header">
                        <div class="section-title">🔑 Issue Entitlement</div>
                    </div>
                    <form id="issueLicenseForm" onsubmit="handleIssueLicense(event)">
                        <div class="form-group">
                            <label>Tenant ID</label>
                            <input type="text" id="licTenantId" value="TENANT_LK_01" required>
                        </div>
                        <div class="form-group">
                            <label>Device ID</label>
                            <input type="text" id="licDeviceId" value="C01" required>
                        </div>
                        <div class="form-group">
                            <label>Plan Type</label>
                            <select id="licPlanType">
                                <option value="Trial">2-Day Trial (48 Hours)</option>
                                <option value="Subscription" selected>Monthly Subscription (LKR 10,000/mo)</option>
                                <option value="Permanent">Permanent Purchase</option>
                            </select>
                        </div>
                        <button type="submit" class="btn-submit">Generate &amp; Sign License</button>
                    </form>
                </div>

                <!-- Verify Payment Card -->
                <div class="section-card">
                    <div class="section-header">
                        <div class="section-title">💳 Verify Subscription Payment</div>
                    </div>
                    <form id="verifyPaymentForm" onsubmit="handleVerifyPayment(event)">
                        <div class="form-group">
                            <label>Tenant ID</label>
                            <input type="text" id="payTenantId" value="TENANT_LK_01" required>
                        </div>
                        <div class="form-group">
                            <label>Amount (LKR)</label>
                            <input type="number" id="payAmount" value="10000.00" step="100.00" required>
                        </div>
                        <div class="form-group">
                            <label>Payment Method</label>
                            <select id="payMethod">
                                <option value="BANK_TRANSFER" selected>Bank Transfer (Slip Verified)</option>
                                <option value="CARD">Credit / Debit Card</option>
                                <option value="CHEQUE">Bank Cheque</option>
                            </select>
                        </div>
                        <div class="form-group">
                            <label>Bank Reference / Slip No</label>
                            <input type="text" id="payRef" placeholder="e.g. BOC-TX-998822">
                        </div>
                        <button type="submit" class="btn-submit" style="background: #059669;">Verify &amp; Extend (30 Days)</button>
                    </form>
                </div>
            </div>
        </div>
    </main>

    <footer>
        Enightx POS Cloud Platform &bull; Provider Portal v1.0.0 &bull; Operational Telemetry
    </footer>

    <script>
        async function loadOverview() {
            try {
                const res = await fetch('/api/v1/provider/overview');
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const data = await res.json();

                // Update KPIs
                document.getElementById('kpiBusinesses').innerText = data.summary.total_businesses;
                document.getElementById('kpiDevices').innerText = data.summary.total_devices;
                document.getElementById('kpiOnline').innerText = data.summary.devices_online;
                document.getElementById('kpiOffline').innerText = data.summary.devices_offline;
                document.getElementById('kpiFrozen').innerText = data.summary.devices_frozen;
                document.getElementById('kpiRevenue').innerText = 'LKR ' + data.summary.total_revenue_lkr.toLocaleString('en-US', {minimumFractionDigits: 2});

                // Render Devices
                const devTbody = document.getElementById('devicesTableBody');
                if (data.devices.length === 0) {
                    devTbody.innerHTML = '<tr><td colspan="7" style="text-align:center; color: var(--text-muted);">No devices registered.</td></tr>';
                } else {
                    devTbody.innerHTML = data.devices.map(d => {
                        let statusClass = d.status;
                        let actionBtn = d.is_frozen 
                            ? `<button class="btn-action success" onclick="unfreezeDevice('${d.device_id}')">Unfreeze</button>`
                            : `<button class="btn-action danger" onclick="freezeDevice('${d.device_id}')">Freeze</button>`;

                        let lastSeen = d.last_seen_at ? new Date(d.last_seen_at).toLocaleTimeString() : 'Never';

                        return `<tr>
                            <td><strong>${d.device_id}</strong></td>
                            <td>${d.tenant_id}</td>
                            <td>${d.branch_id}</td>
                            <td>${d.app_version}</td>
                            <td><span class="status-badge ${statusClass}">${d.status.toUpperCase()}</span></td>
                            <td>${lastSeen}</td>
                            <td>${actionBtn}</td>
                        </tr>`;
                    }).join('');
                }

                // Render Licenses
                const licTbody = document.getElementById('licensesTableBody');
                if (data.licenses.length === 0) {
                    licTbody.innerHTML = '<tr><td colspan="7" style="text-align:center; color: var(--text-muted);">No licenses issued.</td></tr>';
                } else {
                    licTbody.innerHTML = data.licenses.map(lic => {
                        let expires = lic.expires_at ? new Date(lic.expires_at).toLocaleDateString() : 'Permanent';
                        let statusBadge = lic.is_frozen 
                            ? '<span class="status-badge frozen">FROZEN</span>' 
                            : '<span class="status-badge online">ACTIVE</span>';

                        return `<tr>
                            <td><small>${lic.license_id}</small></td>
                            <td>${lic.tenant_id}</td>
                            <td>${lic.device_id}</td>
                            <td><strong>${lic.plan_type}</strong></td>
                            <td>${new Date(lic.issued_at).toLocaleDateString()}</td>
                            <td>${expires}</td>
                            <td>${statusBadge}</td>
                        </tr>`;
                    }).join('');
                }
            } catch (err) {
                console.error('Failed to load overview:', err);
            }
        }

        async function freezeDevice(deviceId) {
            const reason = prompt(`Enter mandatory freeze reason for device ${deviceId}:`, 'Overdue subscription payment');
            if (!reason) return;

            try {
                const res = await fetch(`/api/v1/provider/devices/${deviceId}/freeze`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ reason: reason })
                });
                if (res.ok) {
                    alert(`Device ${deviceId} frozen successfully.`);
                    loadOverview();
                } else {
                    const err = await res.json();
                    alert('Error: ' + (err.detail || 'Freeze failed'));
                }
            } catch (err) {
                alert('Request failed: ' + err.message);
            }
        }

        async function unfreezeDevice(deviceId) {
            if (!confirm(`Are you sure you want to unfreeze device ${deviceId}?`)) return;

            try {
                const res = await fetch(`/api/v1/provider/devices/${deviceId}/unfreeze`, {
                    method: 'POST'
                });
                if (res.ok) {
                    alert(`Device ${deviceId} unfrozen.`);
                    loadOverview();
                } else {
                    const err = await res.json();
                    alert('Error: ' + (err.detail || 'Unfreeze failed'));
                }
            } catch (err) {
                alert('Request failed: ' + err.message);
            }
        }

        async function handleIssueLicense(e) {
            e.preventDefault();
            const tenantId = document.getElementById('licTenantId').value;
            const deviceId = document.getElementById('licDeviceId').value;
            const planType = document.getElementById('licPlanType').value;

            try {
                const res = await fetch('/api/v1/provider/licenses/issue', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        tenant_id: tenantId,
                        device_id: deviceId,
                        plan_type: planType
                    })
                });

                if (res.ok) {
                    const lic = await res.json();
                    alert(`License issued!\nLicense ID: ${lic.license_id}\nPlan: ${lic.plan_type}\nSignature: ${lic.signature}`);
                    loadOverview();
                } else {
                    const err = await res.json();
                    alert('Error: ' + (err.detail || 'Failed to issue license'));
                }
            } catch (err) {
                alert('Request failed: ' + err.message);
            }
        }

        async function handleVerifyPayment(e) {
            e.preventDefault();
            const tenantId = document.getElementById('payTenantId').value;
            const amount = parseFloat(document.getElementById('payAmount').value);
            const method = document.getElementById('payMethod').value;
            const ref = document.getElementById('payRef').value;

            try {
                const res = await fetch('/api/v1/provider/payments/verify', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        tenant_id: tenantId,
                        amount: amount,
                        payment_method: method,
                        reference: ref,
                        verified_by: 'Provider Admin'
                    })
                });

                if (res.ok) {
                    const result = await res.json();
                    alert(`Payment verified!\nReceipt ID: ${result.payment_id}\nSubscription extended until: ${result.valid_until}`);
                    loadOverview();
                } else {
                    const err = await res.json();
                    alert('Error: ' + (err.detail || 'Failed to verify payment'));
                }
            } catch (err) {
                alert('Request failed: ' + err.message);
            }
        }

        // Initialize on load
        loadOverview();
    </script>
</body>
</html>
"""

@router.get("/provider", response_class=HTMLResponse)
def get_provider_dashboard():
    return HTMLResponse(content=PROVIDER_HTML, status_code=200)

