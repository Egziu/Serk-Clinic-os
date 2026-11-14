export interface SetupItem { code: string; label: string; done: boolean; hint: string; path: string }
export interface Insight { code: string; severity: "Info" | "Attention" | "Urgent"; area: string; title: string; detail: string; rule: string; actionLabel: string | null; actionPath: string | null }
export interface InsightsResponse { generatedAt: string; insights: Insight[]; setup: SetupItem[]; setupPercent: number }

export interface ClinicProfile {
  facilityId: string; displayName: string; legalName: string; motto: string; addressLine1: string; addressLine2: string; city: string; region: string;
  country: string; postalCode: string; phone: string; altPhone: string; email: string; website: string; taxId: string; registrationNo: string;
  licenceNo: string; openingHours: string; receiptFooter: string; documentFooter: string; defaultLanguage: string; calendarDisplay: string;
  currency: string; completeness: number; updatedAt: string | null; version: number;
}
