# ADR-0016: Care+ patient messaging, follow-up desk, health tips and social media planner

Date: 2026-11-15 · Status: Accepted (engineering). Clinical protocol review, legal/privacy review and owner approval of real
connectors remain separate gates (SRS v2 Care+ prerequisites).

## Decisions
1. **New module `Serk.Clinic.Care`** (schema `care`) holding consent, templates, messages, replies, care tasks, health tips,
   campaigns, social posts and social metrics. It reads other modules only through published views
   (`scheduling.v_appointment_care`, `patients.v_patient_contact`, `identity.v_staff_reference`, `configuration.v_clinic_identity`).
2. **Consent first.** A patient receives appointment messages only with `consent_reminders` and health tips only with
   `consent_tips`, each recorded with how it was given (verbal, written, Telegram). Every change is an append-only
   `care.consent_events` row. Consent is re-checked immediately before each send; withdrawal cancels anything queued; a
   reply of STOP (or Amharic/Tigrinya equivalents) opts out of everything.
3. **No clinical content in texts.** Templates may only use `{clinic} {clinic_phone} {first_name} {date} {date_et} {time}
   {clinician}`; the visit reason, diagnosis or results can never be inserted. Health tips are general advice, approved by a
   *different* clinician (DB CHECK + service rule) and immutable once approved.
4. **Automation:** confirmation on booking, reminder 24 h before, follow-up 24 h after check-in, re-booking invitation
   after a no-show, and campaigns. One message per purpose/appointment/patient (unique key), quiet hours 20:00–08:00
   facility time, three attempts with back-off, delivery states visible to staff. At-least-once delivery (documented).
5. **Replies are triaged cautiously:** concern words or a rating of 1–2 create a High care task due now; any free text needs
   a person; the bot never answers medical questions and points to emergency services. Tasks close only with a written
   outcome.
6. **Channels:** a sandbox (records, sends nothing) is the default for SMS and Telegram. A real Telegram Bot API sender and a
   webhook (secret-token authenticated) exist but are **off** unless `Care:Telegram:Enabled`, a bot token and a webhook
   secret are configured — an owner decision with credentials. No SMS gateway is connected yet (Ethio telecom / aggregator
   choice and contract needed).
7. **Telegram linking** uses an 8-character one-time code (30 minutes, stored hashed) that the patient sends to the bot.
   Code and chat lookups happen through narrow SECURITY DEFINER functions; the tables are not granted to the app role.
8. **Social media** is a planner and tracker only: nothing is posted to any platform automatically. A post that shows a
   patient needs a recorded media-consent reference (DB CHECK). Results are entered by staff (or exported).
9. **Branding uploads** up to 20 MB and 4096 × 4096 px (owner request); only the upload routes are exempt from the 1 MB
   request limit; PDF rendering is serialised so large images are rendered one at a time.

## Consequences / open
- Sending real messages needs: owner approval, a Telegram bot (BotFather) and/or an SMS gateway contract, secrets in the
  environment, a privacy notice to patients, and clinical review of templates, word lists and Amharic/Tigrinya texts.
- Not built: automatic SMS inbound, WhatsApp/Facebook connectors, posting to social platforms, satisfaction surveys beyond
  the 1–5 rating, staff on-call rota for urgent replies.
