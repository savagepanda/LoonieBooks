using System.Text.Json.Serialization;

namespace MapleLedger.Fdx;

// Enumerations from the FDX core Accounts / Transactions schemas. Values serialize using the
// FDX wire spelling (e.g. DEPOSIT_ACCOUNT, CREDITCARD) regardless of the C# member name.

[JsonConverter(typeof(JsonStringEnumConverter<AccountCategory>))]
public enum AccountCategory
{
    [JsonStringEnumMemberName("DEPOSIT_ACCOUNT")] DepositAccount,
    [JsonStringEnumMemberName("INVESTMENT_ACCOUNT")] InvestmentAccount,
    [JsonStringEnumMemberName("LOAN_ACCOUNT")] LoanAccount,
    [JsonStringEnumMemberName("LOC_ACCOUNT")] LocAccount,
    [JsonStringEnumMemberName("INSURANCE_ACCOUNT")] InsuranceAccount,
    [JsonStringEnumMemberName("ANNUITY_ACCOUNT")] AnnuityAccount,
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountType>))]
public enum AccountType
{
    // Deposit
    [JsonStringEnumMemberName("CHECKING")] Checking,
    [JsonStringEnumMemberName("SAVINGS")] Savings,
    [JsonStringEnumMemberName("CD")] Cd,
    [JsonStringEnumMemberName("MONEYMARKET")] MoneyMarket,

    // Line of credit
    [JsonStringEnumMemberName("CREDITCARD")] CreditCard,
    [JsonStringEnumMemberName("LINEOFCREDIT")] LineOfCredit,
    [JsonStringEnumMemberName("HOMELINEOFCREDIT")] HomeLineOfCredit,

    // Loan
    [JsonStringEnumMemberName("MORTGAGE")] Mortgage,
    [JsonStringEnumMemberName("AUTOLOAN")] AutoLoan,
    [JsonStringEnumMemberName("STUDENTLOAN")] StudentLoan,
    [JsonStringEnumMemberName("LOAN")] Loan,

    // Investment. Canadian registered plans (RRSP, TFSA, FHSA, ...) are tracked as
    // BROKERAGE until the Canadian FDX profile's account-type list is confirmed.
    [JsonStringEnumMemberName("BROKERAGE")] Brokerage,

    [JsonStringEnumMemberName("OTHER")] Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountStatus>))]
public enum AccountStatus
{
    [JsonStringEnumMemberName("OPEN")] Open,
    [JsonStringEnumMemberName("CLOSED")] Closed,
    [JsonStringEnumMemberName("PENDINGOPEN")] PendingOpen,
    [JsonStringEnumMemberName("PENDINGCLOSE")] PendingClose,
    [JsonStringEnumMemberName("RESTRICTED")] Restricted,
}

/// <summary>Direction of a transaction from the account holder's perspective.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DebitCreditMemo>))]
public enum DebitCreditMemo
{
    [JsonStringEnumMemberName("DEBIT")] Debit,
    [JsonStringEnumMemberName("CREDIT")] Credit,
    [JsonStringEnumMemberName("MEMO")] Memo,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionStatus>))]
public enum TransactionStatus
{
    [JsonStringEnumMemberName("AUTHORIZATION")] Authorization,
    [JsonStringEnumMemberName("MEMO")] Memo,
    [JsonStringEnumMemberName("PENDING")] Pending,
    [JsonStringEnumMemberName("POSTED")] Posted,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionType>))]
public enum TransactionType
{
    [JsonStringEnumMemberName("ADJUSTMENT")] Adjustment,
    [JsonStringEnumMemberName("ATMDEPOSIT")] AtmDeposit,
    [JsonStringEnumMemberName("ATMWITHDRAWAL")] AtmWithdrawal,
    [JsonStringEnumMemberName("BILLPAYMENT")] BillPayment,
    [JsonStringEnumMemberName("CHECK")] Check,
    [JsonStringEnumMemberName("DEPOSIT")] Deposit,
    [JsonStringEnumMemberName("DIRECTDEPOSIT")] DirectDeposit,
    [JsonStringEnumMemberName("DIVIDEND")] Dividend,
    [JsonStringEnumMemberName("FEE")] Fee,
    [JsonStringEnumMemberName("INTEREST")] Interest,
    [JsonStringEnumMemberName("PAYMENT")] Payment,
    [JsonStringEnumMemberName("POSCREDIT")] PosCredit,
    [JsonStringEnumMemberName("POSDEBIT")] PosDebit,
    [JsonStringEnumMemberName("PURCHASE")] Purchase,
    [JsonStringEnumMemberName("TRANSFER")] Transfer,
    [JsonStringEnumMemberName("WITHDRAWAL")] Withdrawal,
}
