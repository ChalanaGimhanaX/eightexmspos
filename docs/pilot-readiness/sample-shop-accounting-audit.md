# Sample Shop 1-Day Accounting & Cash Reconciliation Audit

**Shop Profile:** Nimal Motors (Pvt) Ltd - Colombo Branch  
**Counter:** C01 | **Cashier:** Nuwan Perera (cashier1) | **Date:** 2026-09-22  
**Business Hours:** 08:30 AM – 06:30 PM  

---

## 1. Opening Cash Float

- **Float Declared at 08:30 AM**: `LKR 15,000.00`
- **Breakdown**: 5x LKR 1000, 10x LKR 500, 40x LKR 100, 20x LKR 50

---

## 2. Daily Transactions Summary (100 Transactions Simulated)

| Category | Count | Total Value (LKR) | Cash Impact (LKR) | Notes |
| :--- | :--- | :--- | :--- | :--- |
| **Cash Sales** | 68 | `LKR 184,250.00` | `+184,250.00` | Full cash checkout at counter |
| **Card Payments (Visa/Master)** | 18 | `LKR 72,400.00` | `0.00` | External POS terminal; does not touch drawer |
| **QR Payments (LANKAQR)** | 6 | `LKR 16,800.00` | `0.00` | Bank app direct transfer |
| **Credit Sales ('Naya')** | 8 | `LKR 43,500.00` | `0.00` | Added to customer credit ledger balances |
| **Credit Debt Collections** | 3 | `LKR 21,000.00` | `+21,000.00` | Debt settlement received in cash |
| **Customer Returns / Cash Refunds** | 2 | `LKR 3,500.00` | `-3,500.00` | Faulty brake switch & oil filter returned |
| **Mid-Shift Cash In** | 1 | `LKR 5,000.00` | `+5,000.00` | Additional change notes added from manager |
| **Mid-Shift Cash Out** | 2 | `LKR 8,200.00` | `-8,200.00` | Courier delivery (LKR 3,200) + Lunch tea (LKR 5,000) |
| **Change Given to Customers** | - | - | `-12,450.00` | Net change disbursed from tender overpayments |

---

## 3. Cash Drawer End-of-Day Reconciliation Formula

$$\begin{aligned}
\text{Expected Drawer Cash} &= \text{Opening Float} \\
&\quad + \text{Cash Sales Received} \\
&\quad + \text{Credit Debt Collections} \\
&\quad + \text{Mid-Shift Cash In} \\
&\quad - \text{Change Given} \\
&\quad - \text{Customer Cash Refunds} \\
&\quad - \text{Mid-Shift Cash Out}
\end{aligned}$$

### Numerical Calculation:
$$\begin{aligned}
\text{Expected Cash} &= 15,000.00 + 184,250.00 + 21,000.00 + 5,000.00 - 12,450.00 - 3,500.00 - 8,200.00 \\
&= \mathbf{LKR\ 201,100.00}
\end{aligned}$$

---

## 4. Physical Cash Count at Close

- **Counted Notes & Coins**:
  - $5000 \times 28 = 140,000.00$
  - $1000 \times 45 = 45,000.00$
  - $500 \times 22 = 11,000.00$
  - $100 \times 44 = 4,400.00$
  - $50 \times 14 = 700.00$
- **Total Counted Cash**: `LKR 201,100.00`
- **Expected Cash**: `LKR 201,100.00`
- **Cash Variance**: $\mathbf{LKR\ 0.00}$ (Perfect Match)

---

## 5. Audit Sign-off

- **Cashier Signature**: Nuwan Perera (`cashier1`)
- **Manager Approval**: Asanka Silva (`manager`)
- **System Shift ID**: `shift_col_20260922_001`
- **Z-Report Generated**: `2026-09-22T18:35:12+05:30`
