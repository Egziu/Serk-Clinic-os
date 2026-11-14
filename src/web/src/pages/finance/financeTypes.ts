// Types for billing, treasury, finance and owner APIs.
export interface Service { id: string; code: string; name: string; unitPrice: number; currency: string; version: number }
export interface InvoiceLine { serviceCode: string; description: string; quantity: number; unitPrice: number; lineTotal: number }
export interface Payment { id: string; invoiceId: string; tender: string; provider: string | null; providerReference: string | null; amount: number; currency: string; status: string; receiptSerial: string | null; refundedAmount: number; createdAt: string; verifiedAt: string | null; businessDate: string | null; settled: boolean }
export interface Invoice {
  id: string; serial: string | null; status: string; patientId: string; patientDisplayName: string; patientMrn: string; currency: string;
  grossAmount: number; discountAmount: number; totalAmount: number; paidAmount: number; balanceDue: number; discountReason: string | null;
  createdAt: string; issuedAt: string | null; businessDate: string | null; version: number; lines: InvoiceLine[]; payments: Payment[];
}
export interface CashSession { id: string; cashierUserId: string; status: string; openingFloat: number; openedAt: string; businessDate: string; cashReceipts: number; cashRefunds: number; expectedAmount: number | null; countedAmount: number | null; variance: number | null; varianceExplanation: string | null; varianceApproved: boolean; closedAt: string | null; version: number }
export interface CloseCheck { controlId: string; result: "Pass" | "Fail" | "Conditional"; severity: string; detail: string }
export interface CloseSnapshot {
  facilityName: string; businessDateLocal: string; timezone: string; currency: string; issuedServiceCharges: number; discounts: number; postedServiceRevenue: number;
  verifiedCollections: { cash: number; electronic: number; total: number };
  cashDrawer: { openingFloat: number; verifiedCashReceipts: number; cashRefunds: number; expected: number; counted: number; variance: number; varianceApproved: boolean; sessions: number };
  refundsActuallyPaid: number; outstandingAccountsReceivable: number; unsettledProviderClearing: number; journalDebits: number; journalCredits: number;
  invoicesIssued: number; receiptsIssued: number; exceptions: { code: string; count: number; amount: number; approved: boolean }[];
}
export interface ClosePreview { businessDate: string; snapshot: CloseSnapshot; snapshotSha256: string; checks: CloseCheck[]; canSubmit: boolean; fullyReconciled: boolean }
export interface DailyClose { id: string; serial: string; businessDate: string; closeVersion: number; status: string; fullyReconciled: boolean; snapshot: CloseSnapshot; snapshotSha256: string; comment: string | null; submittedBy: string; submittedAt: string; decidedAt: string | null; decisionReason: string | null; checks: CloseCheck[]; digest: string }
export interface ApprovalItem { kind: "Discount" | "Refund" | "CashVariance"; id: string; reference: string; amount: number; reason: string; requestedAt: string; requestedByMe: boolean }
export interface TrialBalance { asOf: string; currency: string; lines: { code: string; name: string; type: string; debits: number; credits: number; balance: number }[]; totalDebits: number; totalCredits: number; balanced: boolean }
export interface Trace { invoiceId: string; serial: string | null; currency: string; steps: { step: string; reference: string; status: string; amount: number | null; at: string | null; detail: string }[] }
export interface Subscription { id: string; channel: string; maskedAddress: string | null; status: string; createdAt: string; verifiedAt: string | null }
export interface Delivery { id: string; closeId: string; closeSerial: string; businessDate: string; channel: string; status: string; attempts: number; providerMessageId: string | null; lastError: string | null; updatedAt: string }

export const money = (v: number) => v.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
export const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" }) : "—");
