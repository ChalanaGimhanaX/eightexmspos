# Enightx POS - Cashier Quick-Guide / මුදල් අයකැමි කෙටි මාර්ගෝපදේශය

A 1-page quick reference for daily checkout operations, keyboard shortcuts, and shift handling.

---

## ⌨️ Keyboard Shortcuts / කෙටිමං යතුරු

| Key / යතුර | Action (English) | ක්‍රියාව (සිංහල) |
| :--- | :--- | :--- |
| **`F12`** | **Fast Cash Checkout** | ඉක්මන් මුදල් ගෙවීම (Pay Cash) |
| **`F6`** | **Hold Current Bill** | දැනට ඇති බිල තාවකාලිකව රඳවා තැබීම |
| **`F7`** | **Recall Held Bill** | රඳවා තැබූ බිල නැවත ලබා ගැනීම |
| **`Enter`** | **Add Scanned Item to Cart** | බාර්කෝඩ් අයිතමය බිලට එක් කිරීම |
| **`F2`** | **Customer Search / Credit** | පාරිභෝගික ණය / නය ගිණුම තේරීම |
| **`F8`** | **Shift Cash In / Out** | මුදල් ලාච්චුවට මුදල් දැමීම / ගැනීම |
| **`F10`** | **Close Shift (Z-Report)** | දවසේ ශිෆ්ට් එක අවසන් කිරීම |

---

## 🛒 Daily Checkout Steps / දෛනික බිල්පත් පියවර

### 1. Fast Cash Sale / සාමාන්‍ය මුදල් බිල්පතක් නිකුත් කිරීම
1. Scan part barcode or type barcode number and press **`Enter`**.
   *(බාර්කෝඩ් එක ස්කෑන් කරන්න හෝ අංකය යොදා Enter ඔබන්න)*
2. Adjust quantity using `+` or `-` buttons if needed.
   *(අවශ්‍ය නම් ප්‍රමාණය වෙනස් කරන්න)*
3. Press **`F12`** (Pay Cash) or enter tender amount.
   *(F12 ඔබන්න හෝ ලැබුණු මුදල ඇතුළත් කරන්න)*
4. Receipt prints and cash drawer automatically kicks open.
   *(රිසිට්පත මුද්‍රණය වී මුදල් ලාච්චුව ස්වයංක්‍රීයව විවෘත වේ)*

### 2. Park / Hold a Bill (`F6`) / බිලක් තාවකාලිකව නැවැත්වීම
- If a customer needs to pick another item while at the counter:
  - Press **`F6` (Hold Bill)** -> Add optional note -> Current cart is saved safely.
  - Serve next customer immediately.
  - When customer returns, press **`F7` (Recall Held)** and resume billing.

### 3. Credit ('Naya') Sale / ණය බිල්පතක් නිකුත් කිරීම
1. Add items to cart.
2. Select customer via **`👥 Customers`** button or **`F2`**.
3. Choose **`Credit / Naya`** as payment method.
4. Bill is finalized; customer ledger balance increases automatically.

### 4. Mid-Shift Cash In / Out (`F8`) / මුදල් ලාච්චුවෙන් මුදල් ගැනීම හෝ දැමීම
- To pay a delivery or supplier from drawer:
  - Click **`💵 Cash In/Out`**.
  - Select **`Cash Out`** -> Enter amount (e.g. LKR 2,500) -> Enter mandatory reason -> Save.
  - Reason and cashier name are recorded in the audit trail.

### 5. End of Day: Count & Close (`F10`) / දවසේ ශිෆ්ට් එක අවසන් කිරීම
1. Click **`🔒 Close Shift`** in top bar.
2. Count the physical cash in drawer and enter total counted notes/coins.
3. System compares:
   $$\text{Expected Cash} = \text{Opening Float} + \text{Cash In} - \text{Cash Out} + \text{Cash Received} - \text{Cash Refunds} - \text{Change Given}$$
4. Review variance. If variance is `0.00`, shift is balanced.
5. Click **`Confirm & Close Shift`** -> Z-Report prints automatically.

---

## ⚠️ Emergency Support / හදිසි සහාය
- **Help Desk Hotlines**: `077-1234567` / `011-2345678`
- **Working Hours**: Monday – Sunday, 8:00 AM – 9:00 PM
- **Target Response**: Under 3 hours for critical billing interruptions.
