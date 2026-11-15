# Automation, follow-ups and the notice board

## What runs by itself (every few minutes)
- **Booking**: a confirmation goes to patients who agreed to messages.
- **Before the visit**: a main reminder (default 24 h before) and an optional short reminder on the day (default 2 h before).
- **After the visit**: a "How are you feeling?" message. A worrying answer or a low rating creates an urgent call-back task.
- **Missed visit**: an invitation to re-book.
- **Next check-up**: after each attended visit, if nothing else is booked, a routine check-up recall is set (default
  6 months). The patient is invited 14 days before the due date. If it is still not booked 14 days after the due date,
  reception gets a call-back task.
- **Quiet hours**: nothing is sent between 20:00 and 08:00 by default. Messages go only to patients who agreed to them.
- **Consent**: check-up invitations need their own tick at reception ("Invitations to book check-ups and reviews").
  It is off until the patient agrees; patients who agreed only to appointment reminders get no recall invitations.

Change any of these in **Care+ → Automation** (owner and clinic administrator). Each branch has its own settings.

## Follow-ups set by the dentist
On the encounter page, use **Next visit**: choose the purpose (review, check-up, cleaning, treatment) and when the
patient should come back (1 week to 1 year). You can add a note for the team; the patient never sees it.

Reception sees every follow-up in **Care+ → Recalls**. A recall changes automatically:
- **Booked** as soon as an appointment is made;
- **Done** when the patient attends;
- back to waiting if that appointment is cancelled or missed.

## Notice board
Open it from **Notice board** in the menu, or from the megaphone button at the top. The red number shows notices waiting
for you.
- Any staff member can post a Normal or Important notice for their branch and choose which roles see it.
- Owners and administrators can also:
  - post **Urgent** notices;
  - **pin** a notice to the top;
  - require everyone to **confirm they read it**;
  - post to **all branches**;
  - see **who has not yet confirmed**.
- Automatic notices appear for:
  - patients waiting for a call-back;
  - recalls to book;
  - yesterday's day close not locked;
  - no backup in the last 7 days.

  They disappear by themselves when the problem is resolved.
- **Never write patient names, MRNs or phone numbers.** The board blocks MRNs and phone numbers, but it cannot detect
  names.

## Limits
- Messaging is in sandbox mode until the owner connects a Telegram bot or an SMS gateway; until then messages are
  recorded but not delivered.
- The default recall timings are product defaults. A clinician should confirm them for your clinic.
