namespace MapleLedger.Web.Client.Help;

/// <param name="Id">Short, stable slug: <c>help?a=&lt;id&gt;</c> opens the article on the help page.</param>
/// <param name="Title">Shown on the help page and as the title of the per-page help dialog.</param>
/// <param name="Category">Groups the article on the help page; categories show in order of first appearance.</param>
/// <param name="Icon">The <c>help-icon-*</c> class (app.css) drawn beside the title.</param>
/// <param name="Summary">One line, shown under the title before the article is opened.</param>
/// <param name="Path">The page the article describes, or null when it describes no single page.</param>
/// <param name="PathLabel">How to name that page in an "Open …" link.</param>
/// <param name="Keywords">Extra search terms. The title, summary and section are searched too, so these
/// are the words a reader might type that the article's own title and summary don't contain.</param>
/// <param name="DesktopOnly">Listed only in the desktop app; anywhere else it describes controls that aren't there.</param>
public sealed record HelpArticleEntry(
    string Id,
    string Title,
    string Category,
    string Icon,
    string Summary,
    string? Path,
    string? PathLabel,
    string[] Keywords,
    Type ComponentType,
    bool DesktopOnly = false);

/// <summary>The help articles shown on /help and by the help button in the header.</summary>
public static class HelpCatalog
{
    public const string CategoryGettingStarted = "Getting started";
    public const string CategoryImporting = "Accounts & importing";
    public const string CategoryOrganizing = "Organizing your spending";
    public const string CategoryTax = "Tax report";
    public const string CategoryData = "Your data";

    private static readonly HelpArticleEntry[] Articles =
    [
        new("getting-started", "Getting started", CategoryGettingStarted, "start",
            "The first half hour: add your accounts, bring in their transactions, sort them out, then use them.",
            "/", "the dashboard",
            ["start", "first", "new", "begin", "setup", "set up", "welcome", "overview", "tour", "how to", "workflow"],
            typeof(GettingStartedArticle)),

        new("using-help", "Using help", CategoryGettingStarted, "help",
            "Searching these articles, and opening the one for the page you're on from the ? button.",
            "/help", "the help page",
            ["help", "search", "guide", "question", "support", "faq", "button"],
            typeof(UsingHelpArticle)),

        new("accounts", "Accounts", CategoryImporting, "bank",
            "Adding chequing, savings, credit card, loan and investment accounts, keeping their balances right, and your properties' equity.",
            "/accounts", "accounts",
            ["account", "add account", "bank", "chequing", "checking", "savings", "credit card", "line of credit", "heloc", "mortgage", "loan", "investment", "rrsp", "tfsa", "fhsa", "balance", "net worth", "delete", "last 4", "institution", "real estate", "property", "house", "equity", "home value", "secured by", "hpi", "house price index", "mls", "crea", "benchmark", "loan-to-value", "ltv"],
            typeof(AccountsArticle)),

        new("holdings", "Investment holdings", CategoryImporting, "pie",
            "Keeping RRSP, TFSA, FHSA and other investment accounts up to date from the monthly statement or the broker's holdings report.",
            "/holdings", "holdings",
            ["holdings", "positions", "investment", "investments", "brokerage", "broker", "rrsp", "tfsa", "fhsa", "resp", "non-registered", "stocks", "etf", "fund", "mutual fund", "shares", "units", "market value", "book value", "gain", "portfolio", "allocation", "wealthsimple", "questrade", "webbroker", "direct investing", "usd", "exchange rate", "trades", "statement", "pdf", "monthly statement", "drag", "drop"],
            typeof(HoldingsArticle)),

        new("import", "Importing transactions", CategoryImporting, "import",
            "Copy the transactions from your bank's website and paste them in: checking the columns, the preview and importing.",
            "/import", "the import page",
            ["import", "paste", "copy", "bank website", "online banking", "columns", "preview", "date order", "money in", "money out", "wrong way", "sign", "balance", "remembered", "re-import", "overlap"],
            typeof(ImportArticle)),

        new("statement-files", "Statement files and pictures", CategoryImporting, "file",
            "PDF statements (including mortgage and home equity line statements), CSV exports, screenshots, and photos of a paper statement from a webcam or your phone.",
            "/import", "the import page",
            ["pdf", "csv", "statement", "download", "export", "file", "drag", "drop", "mortgage", "heloc", "home equity", "flexline", "line of credit", "principal", "deferred interest", "picture", "photo", "screenshot", "screen capture", "camera", "webcam", "phone", "qr", "ocr", "scan", "encrypted", "password"],
            typeof(StatementFilesArticle)),

        new("undo-duplicates", "Undo an import, and duplicates", CategoryImporting, "copy",
            "Taking back an import into the wrong account, and merging the same transaction imported from two files.",
            "/duplicates", "possible duplicates",
            ["undo", "remove", "wrong account", "wrong file", "duplicate", "duplicates", "twice", "double", "merge", "keep both", "same transaction"],
            typeof(UndoDuplicatesArticle)),

        new("dashboard", "Dashboard", CategoryOrganizing, "house",
            "Income, spending, net cash flow and net worth, by month and by category, with the transactions behind each.",
            "/", "the dashboard",
            ["dashboard", "home", "income", "spending", "expenses", "cash flow", "savings rate", "net worth", "chart", "month", "pie", "bars", "period", "year to date", "summary", "liabilities", "debt", "owed", "property equity"],
            typeof(DashboardArticle)),

        new("transactions", "Transactions", CategoryOrganizing, "list",
            "Finding transactions by date, account, category, tag or text, and changing a whole list at once.",
            "/transactions", "transactions",
            ["transactions", "list", "filter", "search", "find", "date range", "sort", "bulk", "tag all", "all shown", "500"],
            typeof(TransactionsArticle)),

        new("categorizing", "Categories and review", CategoryOrganizing, "tag",
            "How transactions get their category, confirming the automatic ones, and adding your own categories.",
            "/categories", "categories",
            ["category", "categories", "categorize", "categorise", "detail", "subcategory", "sub-category", "review", "needs review", "confirm", "tick", "check mark", "rules", "remembered", "merchant", "same merchant", "similar", "transfer", "credit card payment", "refund", "uncategorized", "custom", "new category"],
            typeof(CategorizingArticle)),

        new("tags", "Tags", CategoryOrganizing, "tags",
            "Labels like Rental or Cottage that group transactions across categories and accounts.",
            "/tags", "tags",
            ["tag", "tags", "label", "rental", "cottage", "vacation", "business", "reimbursable", "renovation", "shared", "group", "project", "rename"],
            typeof(TagsArticle)),

        new("tax-report", "Tax report", CategoryTax, "receipt",
            "Gathering the year's deductions and credits by CRA line: how suggestions work and what the report shows.",
            "/tax", "the tax report",
            ["tax", "taxes", "tax report", "deduction", "credit", "cra", "t1", "return", "line", "claim", "claimable", "medical", "donation", "charitable", "child care", "daycare", "rrsp", "fhsa", "union dues", "moving", "carrying charges", "t2125", "t776", "t777", "self-employment", "tax treatment", "not claimable"],
            typeof(TaxReportArticle)),

        new("tax-review", "Reviewing tax suggestions", CategoryTax, "check",
            "Going through the suggestions one at a time with the keyboard, and finding claims that were missed.",
            "/tax/review", "tax suggestion review",
            ["review", "suggestion", "suggested", "claim it", "not claimable", "keyboard", "shortcut", "missed", "might have been missed", "skip"],
            typeof(TaxReviewArticle)),

        new("receipts", "Receipts", CategoryTax, "camera",
            "Scanning a receipt and attaching it to its transaction, or attaching a file from the tax report.",
            "/receipts/scan", "scan a receipt",
            ["receipt", "receipts", "scan", "photo", "camera", "attach", "attachment", "pdf", "missing receipts", "phone", "slip", "proof", "read with claude"],
            typeof(ReceiptsArticle)),

        new("household", "Household and tax estimate", CategoryTax, "people",
            "Who claims what, whose income is whose, and the rough refund or balance owing for each person.",
            "/tax#household", "the household section",
            ["household", "spouse", "partner", "family", "person", "people", "claimed by", "whose", "estimate", "refund", "balance owing", "owe", "province", "marginal rate", "withholding", "rrsp room", "deduction limit", "pension adjustment", "rental income", "co-owner", "shared account", "instalments"],
            typeof(HouseholdArticle)),

        new("claim-rules", "Medical window, business use and claim year", CategoryTax, "calendar",
            "The 12-month medical window, claiming part of an expense, and moving a claim to another tax year.",
            "/tax", "the tax report",
            ["medical", "12 month", "twelve month", "window", "threshold", "3%", "business use", "percent", "home office", "vehicle", "phone", "internet", "share", "mortgage interest", "rrsp", "60 days", "first 60 days", "march", "claim year", "another year", "carry forward"],
            typeof(ClaimRulesArticle)),

        new("tax-summary", "Printable summary and CSV", CategoryTax, "printer",
            "A summary of the confirmed claims to print, save as PDF or give your accountant, and the CSV export.",
            "/tax/summary", "the printable summary",
            ["print", "printable", "summary", "pdf", "save as pdf", "export", "csv", "excel", "spreadsheet", "accountant", "tax preparer", "appendix"],
            typeof(TaxSummaryArticle)),

        new("your-data", "Where your data is kept, and backups", CategoryData, "database",
            "The desktop app's database, this browser or your own server; backing up, restoring and starting over.",
            "/data", "your data",
            ["data", "storage", "stored", "privacy", "private", "browser", "cloud", "server", "self-hosted", "database", "premium", "sign in", "google", "backup", "back up", "restore", "copy", "move", "another device", "start over", "delete everything", "reset", "clear"],
            typeof(YourDataArticle)),

        new("desktop", "The desktop app and your home network", CategoryData, "laptop",
            "Opening LoonieBooks on other devices at home: sharing, the household passcode, Connect a phone and the certificate.",
            null, null,
            ["desktop", "windows", "app", "launcher", "network", "home network", "lan", "share", "wifi", "phone", "tablet", "ipad", "iphone", "android", "mac", "passcode", "connect a phone", "qr", "certificate", "warning", "not private", "sign out", "upgrade", "update", "new version"],
            typeof(DesktopArticle),
            DesktopOnly: true),
    ];

    /// <summary>
    /// The page each article answers for, for the help button. Kept apart from <see cref="HelpArticleEntry.Path"/>
    /// because the mapping isn't one-to-one: the import page has two articles, and an article's Path is the
    /// page it links to rather than every page it covers.
    /// </summary>
    private static readonly Dictionary<string, string> PageArticles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = "dashboard",
        ["/accounts"] = "accounts",
        ["/holdings"] = "holdings",
        ["/import"] = "import",
        ["/duplicates"] = "undo-duplicates",
        ["/transactions"] = "transactions",
        ["/categories"] = "categorizing",
        ["/tags"] = "tags",
        ["/tax"] = "tax-report",
        ["/tax/review"] = "tax-review",
        ["/tax/summary"] = "tax-summary",
        ["/receipts/scan"] = "receipts",
        ["/data"] = "your-data",
        ["/help"] = "using-help",
    };

    /// <summary>The catalogue as this reader may see it: desktop-only articles only in the desktop app.</summary>
    public static IReadOnlyList<HelpArticleEntry> GetArticles(bool desktop) =>
        desktop ? Articles : Articles.Where(a => !a.DesktopOnly).ToList();

    /// <summary>Sections in the order they first appear, which is the order shown.</summary>
    public static IReadOnlyList<string> GetCategories(bool desktop) =>
        GetArticles(desktop).Select(a => a.Category).Distinct().ToList();

    public static HelpArticleEntry? Find(string? id) =>
        id is null ? null : Articles.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The article for a page, from its path relative to the base (no query or fragment).</summary>
    public static HelpArticleEntry? ForPage(string path)
    {
        var key = "/" + path.Split('?', '#')[0].Trim('/');
        return PageArticles.TryGetValue(key, out var id) ? Find(id) : null;
    }

    /// <summary>
    /// Every word typed has to match the title, summary, section or a keyword, so each extra word narrows
    /// the list, which is what typing one more word is asking for.
    /// </summary>
    public static bool Matches(HelpArticleEntry article, string search)
    {
        var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(t =>
            article.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            article.Summary.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            article.Category.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            article.Keywords.Any(k => k.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
}
