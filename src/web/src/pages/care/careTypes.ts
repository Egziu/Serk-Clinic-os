export interface ConsentEvent { reminders: boolean; tips: boolean; method: string; recordedAt: string }
export interface Contact {
  patientId: string; smsPhone: string; registeredPhone: string; telegramLinked: boolean; preferredChannel: string; language: string;
  consentReminders: boolean; consentTips: boolean; consentMethod: string | null; updatedAt: string | null; version: number; history: ConsentEvent[];
}
export interface CareMessage { id: string; kind: string; channel: string; status: string; dueAt: string; sentAt: string | null; attempts: number; lastError: string | null; patientId: string; mrn: string; patientName: string; body: string; segments: number; simulated: boolean }
export interface CareTask { id: string; patientId: string; mrn: string; patientName: string; phone: string; source: string; priority: string; summary: string; status: string; dueAt: string; createdAt: string; doneAt: string | null; outcome: string | null; replyText: string | null; version: number }
export interface Reply { id: string; patientId: string; mrn: string; patientName: string; channel: string; text: string; rating: number | null; needsAttention: boolean; optOut: boolean; receivedAt: string }
export interface Template { kind: string; language: string; body: string; isEnabled: boolean; isDefault: boolean; version: number; sample: string; segments: number }
export interface Tip { id: string; title: string; body: string; topic: string; language: string; status: string; authorId: string; authorName: string; approvedByName: string | null; approvedAt: string | null; createdAt: string; version: number; segments: number }
export interface Campaign { id: string; tipId: string; tipTitle: string; audience: string; months: number | null; scheduledAt: string; status: string; recipients: number; sent: number; failed: number; skipped: number; createdAt: string }
export interface CareSummary { days: number; scheduled: number; sent: number; failed: number; skipped: number; replies: number; averageRating: number | null; openTasks: number; urgentTasks: number; patientsConsentingReminders: number; patientsConsentingTips: number; telegramLinked: number; telegramLive: boolean; smsLive: boolean; byKind: { kind: string; sent: number; failed: number }[] }
export interface Sandbox { simulation: boolean; telegramLive: boolean; smsLive: boolean; botUsername: string }

export const kindLabel: Record<string, string> = { Confirmation: "Booking confirmation", Reminder: "Reminder (24 h before)", FollowUp: "After-visit follow-up", Rebook: "Missed-visit re-booking", Tip: "Health tip" };
export const langLabel: Record<string, string> = { en: "English", am: "Amharic", ti: "Tigrinya" };
