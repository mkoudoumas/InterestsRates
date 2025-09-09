Interest Rates Calculator

A .NET 8 console application that calculates interest based on extra-bank interest rates published by the Bank of Greece.

📦 Requirements

.NET 8 SDK

Playwright
 (for the Special scraping mode)

One-time Playwright install

After the first build, run this inside your project folder (where bin/Debug/net8.0/playwright.ps1 is located):

powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install


This downloads the browser engines Playwright needs. Without this, the Special mode won’t work.

🚀 Running

From the repo root:

dotnet run --project Rates.Cli


You’ll be guided through:

Amount (€)

Date (from, to) in yyyy-MM-dd format

Method:

1 = Calendar year (Actual/365/366)

2 = Banking (360 days)

Rate type:

1 = Contractual (normal statutory rate)

2 = Overdue (penalty rate for late payment)

At the end you’ll see:

Detailed breakdown (period by period)

Yearly summary

Option to export results to CSV (Excel-friendly, saved to Desktop by default)

⚙️ Configuration

Configuration lives in appsettings.json (in Rates.Cli):

{
  "Source": "Local",                       // Local | Online | Special
  "LocalHtmlPath": "C:\\Rates\\bog_rates.html"
}

Modes explained

Local → Use a saved HTML file (fast, reliable, good for testing).

Online → Try to fetch directly from the Bank of Greece site.

Currently blocked by their server with a 403 Forbidden.

Special → Use Playwright to simulate a real browser, download the page locally, and then parse.

This was our workaround to bypass 403.

First run requires playwright.ps1 install (see above).

🧩 Why three modes?

During development we discovered challenges:

Direct HTTP scraping (Online)
The Bank of Greece blocks requests from plain HTTP clients with a 403 (forbidden).

Local HTML (Local)
Reliable fallback: we can manually save the page and parse it offline.

Browser automation (Special)
The only automated way to fetch fresh data. We use Playwright to look like a real user.
This mode can still be fragile if the site adds stronger anti-bot protection.

By supporting all three, the app is future-proof:

Always works locally,

Can adapt to scraping restrictions,

Provides real-time data if Playwright succeeds.

📄 Example CSV Export

Example file:

sep=,
Amount,RateType,Method
1000,Contractual,CalendarYear

From,To,Days,AnnualPercent,Interest
2023-03-01,2023-06-30,122,7.25,24.27
2023-07-01,2023-09-30,92,7.50,18.90

2023: 43.17 €
TOTAL INTEREST: 43.17 €

🛠️ Known Issues

Online mode blocked with 403 Forbidden.

Special mode requires browsers installed via Playwright.

Bank of Greece may change HTML structure; parsing logic would need updates.

📚 Tech Notes

Language: C# (.NET 8)

Core library: Rates.Core (domain logic)

Scraping: HtmlAgilityPack + Playwright

Configuration: Microsoft.Extensions.Configuration.* stack

CSV export: UTF-8 with BOM (Excel-friendly)
