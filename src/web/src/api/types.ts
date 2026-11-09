export interface Affiliation {
  id: string;
  tenantId: string;
  tenantName: string;
  facilityId: string;
  facilityName: string;
  roles: string[];
}

export interface Session {
  userId: string;
  displayName: string;
  csrfToken: string;
  activeAffiliationId: string | null;
  affiliations: Affiliation[];
  capabilities: string[];
  practitionerId: string | null;
}

export interface PatientSummary {
  id: string;
  mrn: string;
  displayName: string;
  sex: string;
  birthDate: string;
  birthDateIsEstimated: boolean;
  ageYears: number;
}

export interface Allergy {
  id: string;
  substance: string;
  reaction: string;
  severity: string;
  status: string;
  recordedAt: string;
}

export interface PatientDetail extends PatientSummary {
  givenName: string;
  fatherName: string;
  grandfatherName: string;
  phone: string;
  nationalId: string | null;
  allergyStatus: string | null;
  allergies: Allergy[] | null;
  version: number;
}

export interface DuplicateCandidate {
  id: string;
  mrn: string;
  displayName: string;
  birthDate: string;
  strength: "Possible" | "Probable" | "Definite";
  reasons: string[];
}

export interface RegisterResponse {
  outcome: "Created" | "DuplicateReviewRequired";
  patient: PatientDetail | null;
  candidates: DuplicateCandidate[];
}

export interface Practitioner {
  id: string;
  displayName: string;
  professionalTitle: string;
  specialty: string;
}

export interface Appointment {
  id: string;
  patientId: string;
  patientMrn: string;
  patientDisplayName: string;
  patientBirthDate: string | null;
  patientBirthDateIsEstimated: boolean;
  patientSex: string;
  practitionerId: string;
  practitionerDisplayName: string;
  startsAt: string;
  endsAt: string;
  status: string;
  reason: string;
  encounterId: string | null;
  version: number;
}

export interface NoteContent {
  chiefComplaint: string;
  history: string;
  examination: string;
  assessment: string;
  plan: string;
}

export interface AllergySnapshot {
  substance: string;
  reaction: string;
  severity: string;
}

export interface SignedSnapshot {
  versionNo: number;
  kind: "Original" | "Addendum";
  content: NoteContent | null;
  addendumText: string | null;
  addendumReason: string | null;
  signerDisplayName: string;
  signerCredential: string;
  signedAtUtc: string;
  allergyStatusAtSigning: string;
  activeAllergiesAtSigning: AllergySnapshot[];
}

export interface NoteVersion {
  id: string;
  versionNo: number;
  kind: string;
  contentSha256: string;
  integrityVerified: boolean;
  signerDisplayName: string;
  signedAt: string;
  snapshot: SignedSnapshot;
}

export interface PatientBanner {
  id: string;
  mrn: string;
  displayName: string;
  sex: string;
  birthDate: string;
  birthDateIsEstimated: boolean;
  allergyStatus: string;
  allergyStatusUpdatedAt: string | null;
  allergyReviewToken: string;
  activeAllergies: AllergySnapshot[];
}

export interface EncounterDetail {
  id: string;
  status: "InProgress" | "Signed";
  startedAt: string;
  signedAt: string | null;
  isAttending: boolean;
  patient: PatientBanner;
  draft: { content: NoteContent; version: number; updatedAt: string } | null;
  signedVersions: NoteVersion[];
}

export interface EncounterListItem {
  id: string;
  status: string;
  startedAt: string;
  patientId: string;
  patientMrn: string;
  patientDisplayName: string;
  allergyStatus: string;
}

export interface DocumentInfo {
  id: string;
  encounterId: string;
  noteVersionId: string;
  templateCode: string;
  templateVersion: number;
  paperSize: string;
  status: "Draft" | "Final" | "Superseded";
  snapshotSha256: string;
  verificationRef: string;
  createdAt: string;
  finalizedAt: string | null;
  pdfSha256: string | null;
  createdBy?: string;
}
