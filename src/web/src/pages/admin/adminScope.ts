import { createContext, useContext } from "react";

/**
 * Where the administration screens send their requests: the system console (super administrators, all organisations) or
 * the organisation's own staff and role administration (owners; the server confines every call to their organisation).
 */
export interface AdminScope { base: string; organisationMode: boolean }

export const AdminScopeContext = createContext<AdminScope>({ base: "/api/v1/platform", organisationMode: false });

export const useAdminScope = (): AdminScope => useContext(AdminScopeContext);
