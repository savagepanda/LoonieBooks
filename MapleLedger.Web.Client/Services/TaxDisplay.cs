using MapleLedger.Core.Tax;

namespace MapleLedger.Web.Client.Services;

public static class TaxDisplay
{
    public static string KindName(TaxTreatmentKind kind) => kind switch
    {
        TaxTreatmentKind.Deduction => "Deduction",
        TaxTreatmentKind.NonRefundableCredit => "Non-refundable credit",
        TaxTreatmentKind.RefundableCredit => "Refundable credit",
        _ => "Business expense",
    };
}
