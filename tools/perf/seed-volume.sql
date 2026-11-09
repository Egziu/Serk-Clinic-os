-- Synthetic VOLUME dataset for performance measurement (tenant A, Bole facility). Fictional data only.
-- 20,000 patients, ~35,000 appointments over the past 365 days (non-overlapping 15-min slots), 25,000 signed encounters.
\set tenant '0195a000-0000-7000-8000-00000000000a'
\set facility '0195a000-0000-7000-8000-0000000000f1'
INSERT INTO patients.patients (id, tenant_id, mrn, given_name, father_name, grandfather_name, normalized_name, sex, birth_date, birth_date_is_estimated, phone, registered_at_facility_id, created_at, created_by)
SELECT gen_random_uuid(), :'tenant', 'MRN-V' || lpad(i::text, 6, '0'),
       (ARRAY['Abel','Bethlehem','Dawit','Eden','Fikru','Genet','Hiwot','Kebede','Liya','Meron','Nahom','Rahel','Samuel','Tsion','Yonas'])[1 + i % 15],
       'Vol' || i, 'Perf', lower((ARRAY['Abel','Bethlehem','Dawit','Eden','Fikru','Genet','Hiwot','Kebede','Liya','Meron','Nahom','Rahel','Samuel','Tsion','Yonas'])[1 + i % 15] || ' vol' || i || ' perf'),
       (ARRAY['Female','Male'])[1 + i % 2], date '1950-01-01' + (i * 37 % 26000), false, '+2519' || lpad(i::text, 8, '0'),
       :'facility', now() - ((i % 365) || ' days')::interval, gen_random_uuid()
FROM generate_series(1, 20000) i;

INSERT INTO scheduling.appointments (id, tenant_id, facility_id, patient_id, practitioner_id, starts_at, ends_at, status, created_at, created_by, checked_in_at)
SELECT gen_random_uuid(), :'tenant', :'facility', p.id,
       (ARRAY['0195a000-0000-7000-8000-0000000000d1','0195a000-0000-7000-8000-0000000000d2']::uuid[])[1 + s.i % 2],
       date_trunc('hour', now()) - interval '366 days' + (s.i * interval '15 minutes'),
       date_trunc('hour', now()) - interval '366 days' + (s.i * interval '15 minutes') + interval '15 minutes',
       (ARRAY['Completed','Completed','Completed','NoShow','Cancelled','Completed','Completed'])[1 + s.i % 7],
       date_trunc('hour', now()) - interval '370 days' + (s.i * interval '15 minutes'), gen_random_uuid(),
       CASE WHEN s.i % 7 IN (3, 4) THEN NULL ELSE date_trunc('hour', now()) - interval '366 days' + (s.i * interval '15 minutes') END
FROM generate_series(1, 35000) s(i)
JOIN LATERAL (SELECT id FROM patients.patients WHERE tenant_id = :'tenant' AND mrn = 'MRN-V' || lpad((1 + s.i % 20000)::text, 6, '0')) p ON true;

INSERT INTO clinical.encounters (id, tenant_id, facility_id, patient_id, attending_practitioner_id, status, started_at, signed_at)
SELECT gen_random_uuid(), :'tenant', :'facility', a.patient_id, a.practitioner_id, 'Signed', a.checked_in_at, a.checked_in_at + ((5 + a.rn % 90) || ' minutes')::interval
FROM (SELECT patient_id, practitioner_id, checked_in_at, row_number() OVER () AS rn FROM scheduling.appointments
      WHERE tenant_id = :'tenant' AND checked_in_at IS NOT NULL LIMIT 25000) a;
ANALYZE;
