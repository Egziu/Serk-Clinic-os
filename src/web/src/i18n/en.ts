export const en = {
  appName: "SERK Clinic OS",
  signIn: "Sign in",
  signOut: "Sign out",
  username: "Username",
  password: "Password",
  chooseFacility: "Choose the facility you are working at",
  reception: "Reception",
  worklist: "My patients today",
  patientSearch: "Find patient",
  registerPatient: "Register new patient",
  todaysAppointments: "Today's appointments",
  syntheticBanner: "Synthetic demonstration data only — not for real patients",
} as const;

export type MessageKey = keyof typeof en;
