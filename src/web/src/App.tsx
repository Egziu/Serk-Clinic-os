import { useCallback, useEffect, useState } from "react";
import { api, ApiError, setCsrfToken } from "./api/client";
import type { Session } from "./api/types";
import { t } from "./i18n";
import { LoginPage } from "./pages/LoginPage";
import { ReceptionPage } from "./pages/ReceptionPage";
import { WorklistPage } from "./pages/WorklistPage";
import { EncounterPage } from "./pages/EncounterPage";
import { DashboardPage } from "./pages/DashboardPage";
import { SettingsPage, type FacilitySettings } from "./pages/SettingsPage";
import { SignaturePage } from "./pages/SignaturePage";
import { AccountPage } from "./pages/AccountPage";
import { AdminPage } from "./pages/admin/AdminPage";
import { Icon } from "./components/icons";
import { applyAccent, applyThemePreference, loadThemePreference, type ThemePreference } from "./lib/theme";

export type Route =
  | { name: "home" } | { name: "reception" } | { name: "worklist" } | { name: "encounter"; id: string }
  | { name: "dashboard" } | { name: "settings" } | { name: "signature" } | { name: "admin" } | { name: "account" };

function parseRoute(): Route {
  const m = window.location.pathname.match(/^\/encounters\/([0-9a-f-]{36})$/i);
  if (m) return { name: "encounter", id: m[1]! };
  if (window.location.pathname.startsWith("/worklist")) return { name: "worklist" };
  if (window.location.pathname.startsWith("/reception")) return { name: "reception" };
  if (window.location.pathname.startsWith("/dashboard")) return { name: "dashboard" };
  if (window.location.pathname.startsWith("/settings")) return { name: "settings" };
  if (window.location.pathname.startsWith("/signature")) return { name: "signature" };
  if (window.location.pathname.startsWith("/admin")) return { name: "admin" };
  if (window.location.pathname.startsWith("/account")) return { name: "account" };
  return { name: "home" };
}

function routeToPath(r: Route): string {
  return r.name === "encounter" ? `/encounters/${r.id}` : r.name === "home" ? "/" : `/${r.name}`;
}

export function App() {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const [route, setRoute] = useState<Route>(parseRoute);
  const [theme, setTheme] = useState<ThemePreference>(loadThemePreference);
  const [facility, setFacility] = useState<FacilitySettings | null>(null);

  useEffect(() => applyThemePreference(theme), [theme]);

  // Clinic branding (accent colour + logo) once a facility is active.
  useEffect(() => {
    if (!session?.activeAffiliationId || session.mustChangePassword) return;
    api.get<FacilitySettings>("/api/v1/facility/settings")
      .then((r) => { setFacility(r.data); applyAccent(r.data.accentColor); })
      .catch(() => { /* branding is cosmetic; defaults remain */ });
  }, [session?.activeAffiliationId, session?.mustChangePassword]);

  const navigate = useCallback((r: Route) => {
    window.history.pushState(null, "", routeToPath(r));
    setRoute(r);
    document.getElementById("main")?.focus();
  }, []);

  useEffect(() => {
    const onPop = () => setRoute(parseRoute());
    window.addEventListener("popstate", onPop);
    api
      .get<Session>("/api/v1/auth/session")
      .then((r) => {
        setCsrfToken(r.data.csrfToken);
        setSession(r.data);
      })
      .catch((e: unknown) => {
        if (!(e instanceof ApiError && e.status === 401)) console.error("Session check failed");
        setSession(null);
      });
    return () => window.removeEventListener("popstate", onPop);
  }, []);

  const onSession = (s: Session | null) => {
    setCsrfToken(s?.csrfToken ?? null);
    setSession(s);
  };

  const logout = async () => {
    try {
      await api.post("/api/v1/auth/logout");
    } finally {
      onSession(null);
      window.history.pushState(null, "", "/");
    }
  };

  if (session === undefined) return <p className="sr-only" role="status">Loading…</p>;
  // A temporary (administrator-set) password must be replaced before anything else; the server grants nothing meanwhile.
  if (session?.mustChangePassword) {
    return (
      <div className="login-wrap">
        <div className="synthetic-banner" role="note">{t("syntheticBanner")}</div>
        <main id="main" tabIndex={-1}>
          <AccountPage session={session} forced onSession={onSession} />
          <p className="narrow"><button className="btn" onClick={logout}><Icon name="logout" size={16} /> {t("signOut")}</button></p>
        </main>
      </div>
    );
  }
  // Super administrators work without a clinic assignment (system console only); everyone else must choose a facility.
  if (session === null || (session.activeAffiliationId === null && !session.isPlatformAdmin)) {
    return <LoginPage session={session} onSession={onSession} />;
  }

  const can = (cap: string) => session.capabilities.includes(cap);
  const affiliation = session.affiliations.find((a) => a.id === session.activeAffiliationId);
  // Landing page follows the role's primary task: management -> dashboard, clinical staff -> worklist, front desk -> reception.
  const home: Route = can("clinical.encounter.read") ? { name: "worklist" } : can("patient.search") ? { name: "reception" } : can("analytics.read") ? { name: "dashboard" }
    : can("branding.manage") || can("printers.manage") ? { name: "settings" } : session.isPlatformAdmin ? { name: "admin" } : { name: "account" };
  const allowed = (r: Route): boolean =>
    r.name === "reception" ? can("patient.search")
      : r.name === "worklist" || r.name === "encounter" ? can("clinical.encounter.read")
      : r.name === "dashboard" ? can("analytics.read")
      : r.name === "settings" ? can("branding.manage") || can("printers.manage")
      : r.name === "signature" ? can("clinical.note.sign")
      : r.name === "admin" ? session.isPlatformAdmin
      : true;
  const effectiveRoute: Route = route.name === "home" || !allowed(route) ? home : route;
  const navItems: { route: Route; label: string; icon: string; show: boolean }[] = [
    { route: { name: "dashboard" }, label: "Dashboard", icon: "dashboard", show: can("analytics.read") },
    { route: { name: "reception" }, label: t("reception"), icon: "reception", show: can("patient.search") },
    { route: { name: "worklist" }, label: t("worklist"), icon: "worklist", show: can("clinical.encounter.read") },
    { route: { name: "signature" }, label: "My signature", icon: "signature", show: can("clinical.note.sign") },
    { route: { name: "settings" }, label: "Settings", icon: "settings", show: can("branding.manage") || can("printers.manage") },
    { route: { name: "admin" }, label: "System admin", icon: "shield", show: session.isPlatformAdmin },
    { route: { name: "account" }, label: "My account", icon: "key", show: true },
  ];
  const isCurrent = (r: Route) => effectiveRoute.name === r.name || (r.name === "worklist" && effectiveRoute.name === "encounter");

  const chooseFacility = async (affiliationId: string) => {
    if (!affiliationId) return;
    const r = await api.post<Session>("/api/v1/auth/session/affiliation", { affiliationId });
    onSession(r.data);
  };

  return (
    <>
      <a href="#main" className="skip-link">Skip to main content</a>
      <div className="synthetic-banner" role="note">{t("syntheticBanner")}</div>
      <div className="shell">
        <nav className="shell-nav" aria-label="Primary">
          <div className="brand">
            {facility?.logo ? <img src={`/api/v1/branding-assets/${facility.logo.id}/content`} alt="" /> : null}
            <span>{t("appName")}</span>
          </div>
          <ul>
            {navItems.filter((n) => n.show).map((n) => (
              <li key={n.route.name}>
                <button aria-current={isCurrent(n.route) ? "page" : undefined} onClick={() => navigate(n.route)}>
                  <Icon name={n.icon} /> {n.label}
                </button>
              </li>
            ))}
          </ul>
        </nav>
        <div className="shell-main">
          <header className="topbar">
            <div className="context">
              {affiliation ? <><strong>{affiliation.facilityName}</strong> · {affiliation.tenantName}</> : <strong>System administration</strong>}
              {session.isPlatformAdmin && session.affiliations.length > 0 && (
                <>
                  {" "}<label className="sr-only" htmlFor="facility-switch">Work at facility</label>
                  <select id="facility-switch" className="inline-select" value={session.activeAffiliationId ?? ""} onChange={(e) => void chooseFacility(e.target.value)}>
                    <option value="">No facility (system console)</option>
                    {session.affiliations.map((a) => <option key={a.id} value={a.id}>{a.facilityName} — {a.tenantName}</option>)}
                  </select>
                </>
              )}
            </div>
            <div className="btn-row">
              <span className="context">
                Signed in as <strong>{session.displayName}</strong> ({[session.isPlatformAdmin ? "Super admin" : null, ...(affiliation?.roles ?? [])].filter(Boolean).join(", ")})
              </span>
              <div className="theme-switch" role="group" aria-label="Colour theme">
                {([["light", "sun", "Light theme"], ["dark", "moon", "Dark theme"], ["system", "monitor", "Use system theme"]] as const).map(([v, icon, label]) => (
                  <button key={v} type="button" aria-pressed={theme === v} aria-label={label} title={label} onClick={() => setTheme(v)}><Icon name={icon} size={16} /></button>
                ))}
              </div>
              <button className="btn" onClick={logout}><Icon name="logout" size={16} /> {t("signOut")}</button>
            </div>
          </header>
          <main id="main" tabIndex={-1}>
            {effectiveRoute.name === "reception" && <ReceptionPage session={session} />}
            {effectiveRoute.name === "worklist" && <WorklistPage onOpen={(id) => navigate({ name: "encounter", id })} />}
            {effectiveRoute.name === "encounter" && <EncounterPage id={effectiveRoute.id} session={session} onBack={() => navigate({ name: "worklist" })} />}
            {effectiveRoute.name === "dashboard" && <DashboardPage />}
            {effectiveRoute.name === "settings" && <SettingsPage canBrand={can("branding.manage")} canPrinters={can("printers.manage")} onSettingsChanged={setFacility} />}
            {effectiveRoute.name === "signature" && <SignaturePage />}
            {effectiveRoute.name === "admin" && <AdminPage currentUserId={session.userId} />}
            {effectiveRoute.name === "account" && <AccountPage session={session} forced={false} onSession={onSession} />}
          </main>
        </div>
      </div>
    </>
  );
}
