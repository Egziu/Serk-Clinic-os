# Clinical review required (nothing here is clinically approved)

Status of every item: **PENDING CLINICIAN REVIEW** — no licensed clinician has reviewed this software.

1. Signing preconditions: chief complaint + assessment required; allergy status must be recorded; allergy-review attestation (`Encounter.Sign`).
2. Who may sign/add addenda: attending only for the original; any active practitioner with sign capability for addenda; 5-character minimum reason.
3. Duplicate matching thresholds (`DuplicateMatcher`): ±366-day window for estimated DOB; phone+given name = Probable; national ID = Definite.
4. Allergy model: free-text substance; severities Mild/Moderate/Severe/Unknown; inactivation returns status to Unknown; roles allowed to record "no known allergies" (nurse, clinician, dentist).
5. Role visibility (`RoleCatalog` v1): reception has no allergy view; nurses cannot read documents.
6. Printed consultation summary template v2 content, wording ("Not recorded.", "AMENDED", allergy warning lines), A4/A5 layout.
7. Age display (whole years; infants), estimated DOB presentation, 130-year maximum.
8. Ethiopian calendar display and the absence of Ethiopian local time-of-day on patient-facing documents.
9. 30-minute backdating tolerance for appointments.
10. Amharic/Tigrinya UI and template wording — catalogues intentionally empty until qualified translators review them.

Each item needs: named reviewer, date, source/guideline version, decision, and resulting test updates — recorded outside automated output.
