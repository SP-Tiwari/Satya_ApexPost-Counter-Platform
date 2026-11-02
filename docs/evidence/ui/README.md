# UI modernization evidence

Browser check of the modernized ApexPost interface, recorded from an actual run on 2026-11-02. All data is fictional demo data.

## How it was run

| Item | Value |
|---|---|
| Application | The working tree with the UI changes, started with `./scripts/run-demo.ps1 -ConnectionString …` |
| Database | Disposable `ApexPost_Test_UiReview` in `E:\SqlData\ApexPost\Tests`, dropped afterwards. `ApexPostDb` was not used |
| Settings | The same non-secret values as [run-postman.ps1](../m7/run-postman.ps1): tax rates, the `Cash` payment method, shipping tax category and weight limit, the low-stock threshold and the money-order threshold |
| Browser | Microsoft Edge, headless, driven through the DevTools protocol by a one-off script, not part of the test suites |
| Widths | Desktop 1440 × 900, tablet 820 × 1180, phone 390 × 844 |
| Logins | `demo-staff-01` Counter Clerk, `02` Branch Manager, `03` Auditor, `04` Administrator, and signed out |

The full machine-readable result is [ui-check-report.json](ui-check-report.json).

## Results

**39 page loads, with no script errors and no Content Security Policy violations. No page scrolls horizontally at any width.** The only logged message is the intended 403 when the Auditor opens Administration. The create-form check also logs the intended 400 when an empty sale is submitted.

| Check | Result |
|---|---|
| Role navigation | Each role sees only the links its controllers allow. The Auditor is refused Administration with the Access denied page |
| Sidebar | Collapses to icons on desktop and opens as an overlay on a phone |
| Tables | Transactions: 54 rows, 10 per page, five page buttons with Previous and Next. Search filters the rows, and a header click sorts ascending |
| Confirmation | Void with an empty reason is stopped by the form. With a reason, the dialog *Void transaction* opens, and Cancel leaves the sale untouched |
| Server validation | An empty sale returns the three server errors, listed at the top and under each field |
| Dialog form | Administration › Staff › Edit opens *Edit DEMO Staff 01* |

## Screenshots

| Page | Desktop | Tablet | Phone |
|---|---|---|---|
| Signed-out home | [desktop](anonymous-home-desktop.jpg) | | [phone](anonymous-home-mobile.jpg) |
| Sign-in | [desktop](anonymous-signin-desktop.jpg) | | |
| Branch Manager dashboard | [desktop](manager-dashboard-desktop.jpg) | [tablet](manager-dashboard-tablet.jpg) | [phone](manager-dashboard-mobile.jpg) |
| Counter Clerk dashboard | [desktop](clerk-dashboard-desktop.jpg) | | |
| Auditor dashboard | [desktop](auditor-dashboard-desktop.jpg) | | |
| Create transaction | [desktop](clerk-create-desktop.jpg) | | [phone](clerk-create-mobile.jpg) |
| Create transaction, server validation | [desktop](clerk-create-validation-desktop.jpg) | | |
| Transactions | [desktop](manager-transactions-desktop.jpg) | | [phone](manager-transactions-mobile.jpg) |
| Transaction details | [desktop](manager-details-desktop.jpg) | | [phone](manager-details-mobile.jpg) |
| Void confirmation | [desktop](manager-void-confirm-desktop.jpg) | | |
| Shipping label | [desktop](manager-label-desktop.jpg) | | |
| Money orders | [desktop](manager-money-orders-desktop.jpg) | | |
| Catalogue | [desktop](clerk-catalogue-desktop.jpg) | | |
| Adjust stock | [desktop](manager-stock-desktop.jpg) | | |
| Daily sales report | [desktop](manager-daily-sales-desktop.jpg) | | |
| Low-stock report | [desktop](manager-low-stock-desktop.jpg) | | |
| Branch auditor report | [desktop](auditor-branch-auditor-desktop.jpg) | | [phone](auditor-branch-auditor-mobile.jpg) |
| Shipping volume report | [desktop](auditor-shipping-volume-desktop.jpg) | | |
| Administration | [desktop](admin-administration-desktop.jpg) | | [phone](admin-administration-mobile.jpg) |
| Edit staff dialog | [desktop](admin-edit-staff-desktop.jpg) | | |
| Access denied | [desktop](auditor-forbidden-admin-desktop.jpg) | | |
| Sidebar collapsed | [desktop](manager-sidebar-collapsed-desktop.jpg) | | |
| Sidebar open | | | [phone](manager-sidebar-open-mobile.jpg) |

Pages without a tablet or phone screenshot were still loaded and checked at those widths, as the report shows.

## Limitations

- **One run, one machine.** The check is not part of the automated suites, which use no browser engine.
- **Full-page screenshots.** The sidebar is fixed to the window, so in a screenshot taller than the window it stops at the window's height. In a browser it always fills the window.
- **Tables without search.** The report records 0 interactive tables for Daily sales, transaction details and the dashboard. Those tables are short and deliberately plain, so they have no search, sort or paging.
- **Courier tracking.** The demo configures no courier service, so the dashboard's shipment panel shows its *not configured* state.
