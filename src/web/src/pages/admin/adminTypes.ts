// Types for the system administration API (/api/v1/platform/*).
export type Sensitivity = "Standard" | "PatientData" | "Clinical" | "Administrative";
export interface CapabilityInfo { code: string; group: string; label: string; description: string; sensitivity: Sensitivity }
export interface RoleInfo { id: string | null; tenantId: string | null; code: string; name: string; description: string; capabilities: string[]; isBuiltIn: boolean; isActive: boolean; version: number; assignedCount: number }
export interface Catalog { capabilities: CapabilityInfo[]; builtInRoles: RoleInfo[] }
export interface Facility { id: string; tenantId: string; code: string; name: string; addressLine: string; phone: string; licenseNumber: string; timeZone: string }
export interface Organisation { id: string; name: string; createdAt: string; facilities: Facility[]; staffCount: number; customRoleCount: number }
export interface StaffSummary { id: string; username: string; displayName: string; isActive: boolean; isLocked: boolean; mustChangePassword: boolean; isPlatformAdmin: boolean; lastLoginAt: string | null; activeAssignments: number; organisations: string[] }
export interface Assignment {
  id: string; tenantId: string; tenantName: string; facilityId: string; facilityName: string; roles: string[];
  grantedCapabilities: string[]; revokedCapabilities: string[]; effectiveCapabilities: string[];
  isActive: boolean; isCurrentlyValid: boolean; validFrom: string; validUntil: string | null; version: number;
}
export interface PractitionerProfile { id: string; tenantId: string; tenantName: string; professionalTitle: string; specialty: string; licenseNumber: string; licenseExpiresOn: string }
export interface StaffDetail { summary: StaffSummary; assignments: Assignment[]; practitionerProfiles: PractitionerProfile[]; activeSessions: number; createdAt: string; passwordChangedAt: string | null }
export interface Preview { effective: CapabilityInfo[]; bySensitivity: Record<Sensitivity, number>; warnings: string[] }
export interface SuperAdmin { userId: string; username: string; displayName: string; isActive: boolean; grantedAt: string; grantedBy: string | null; revokedAt: string | null }
export interface Change { id: string; occurredAt: string; actor: string; action: string; targetType: string; targetId: string | null; tenantId: string | null; before: string | null; after: string | null; reason: string | null }
export interface Overview { organisations: number; facilities: number; staff: number; activeStaff: number; lockedAccounts: number; pendingPasswordChanges: number; platformAdmins: number; customRoles: number; activeAssignments: number; changesLast7Days: number }

export const SENSITIVITY_LABEL: Record<Sensitivity, string> = {
  Standard: "Operational",
  PatientData: "Patient data",
  Clinical: "Clinical record",
  Administrative: "Administration",
};

/** Badge with text (never colour alone). */
export function sensitivityClass(s: Sensitivity): string {
  return s === "Clinical" ? "badge-danger" : s === "Administrative" ? "badge-warning" : s === "PatientData" ? "badge-info" : "badge-neutral";
}

export const fmtDateTime = (iso: string | null) => (iso ? new Date(iso).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" }) : "—");

/** Strong random temporary password (shown once to the administrator, must be changed at first sign-in). */
export function generateTemporaryPassword(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const body = Array.from(bytes, (b) => alphabet[b % alphabet.length]).join("");
  return `${body.slice(0, 5)}-${body.slice(5, 10)}-${body.slice(10, 16)}`;
}
