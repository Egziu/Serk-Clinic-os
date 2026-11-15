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
import { BillingPage } from "./pages/finance/BillingPage";
import { FinancePage } from "./pages/finance/FinancePage";
import { OwnerPage } from "./pages/finance/OwnerPage";
import { SetupPage } from "./pages/setup/SetupPage";
import { TeamPage } from "./pages/team/TeamPage";
import { DataPage } from "./pages/data/DataPage";
import { CommandPalette, type Command } from "./components/CommandPalette";
import { CarePage } from "./pages/care/CarePage";
import { SocialPage } from "./pages/social/SocialPage";
import { Icon } from "./components/icons";
import { applyAccent, applyThemePreference, loadThemePreference, type ThemePreference } from "./lib/theme";

export type Route =
  | { name: "home" } | { name: "reception" } | { name: "worklist" } | { name: "encounter"; id: string }
  | { name: "dashboard" } | { name: "settings" } | { name: "signature" } | { name: "admin" } | { name: "account" }
  | { name: "billing" } | { name: "finance" } | { name: "owner" } | { name: "setup" } | { name: "team" } | { name: "data" } | { name: "care" } | { name: "social" };

function parseRoute(): Route {
  const m = window.location.pathname.match(/^\/encounters\/([0-9a-f-]{36})$/i);
  if (m) return { name: "encounter", id: m[1]! };
  if (window.location.pathname.startsWith("/worklist")) return { name: "worklist" };
  if (window.location.pathname.startsWith("/reception")) return { name: "reception" };
  if (window.location.pathname.startsWith("/dashboard")) return { name: "dashboard" };
  if (window.location.pathname.startsWith("/settings")) return { name: "settings" };
  if (window.location.pathname.startsWith("/signature")) return { name: "signature" };
  if (window.location.pathname.startsWith("/admin")) return { name: "admin" };
  if (window.location.pathname.startsWith("/billing")) return { name: "billing" };
  if (window.location.pathname.startsWith("/finance")) return { name: "finance" };
  if (window.location.pathname.startsWith("/owner")) return { name: "owner" };
  if (window.location.pathname.startsWith("/account")) return { name: "account" };
  if (window.location.pathname.startsWith("/setup")) return { name: "setup" };
  if (window.location.pathname.startsWith("/team")) return { name: "team" };
  if (window.location.pathname.startsWith("/data")) return { name: "data" };
  if (window.location.pathname.startsWith("/care")) return { name: "care" };
  if (window.location.pathname.startsWith("/social")) return { name: "social" };
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
  const [paletteOpen, setPaletteOpen] = useState(false);

  // Ctrl/⌘+K opens the jump-to palette from anywhere.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); setPaletteOpen((o) => !o); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  useEffect(() => applyThemePreference(theme), [theme]);

  // Clinic branding (accent colour + logo) once a facility is active.
  useEffect(() => {
    if (!session?.activeAffiliationId || session.mustChangePassword) return;
    api.get<FacilitySettings>("/api/v1/facility/settings")
      .then((r) => { setFacility(r.data); applyAccent(r.data.accentColor); })
      .catch(() => { /* branding is cosmetic; defaults remain */ });
  }, [session?.activeAffiliationId, session?.mustChangePassword]);

  const navigatePath = useCallback((path: string) => {
    window.history.pushState(null, "", path);
    setRoute(parseRoute());
    document.getElementById("main")?.focus();
  }, []);

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
  const home: Route = can("clinical.encounter.read") ? { name: "worklist" } : can("billing.payment.collect") ? { name: "billing" } : can("patient.search") ? { name: "reception" }
    : can("analytics.read") ? { name: "dashboard" } : can("finance.close") ? { name: "finance" }
    : can("branding.manage") || can("printers.manage") ? { name: "settings" } : session.isPlatformAdmin ? { name: "admin" } : { name: "account" };
  const allowed = (r: Route): boolean =>
    r.name === "reception" ? can("patient.search")
      : r.name === "worklist" || r.name === "encounter" ? can("clinical.encounter.read")
      : r.name === "dashboard" ? can("analytics.read")
      : r.name === "settings" ? can("branding.manage") || can("printers.manage")
      : r.name === "signature" ? can("clinical.note.sign")
      : r.name === "admin" ? session.isPlatformAdmin
      : r.name === "billing" ? can("billing.invoice.write")
      : r.name === "finance" ? can("finance.read")
      : r.name === "owner" ? can("owner.reports.read")
      : r.name === "care" ? can("care.desk") || can("care.manage") || can("care.tips.write")
      : r.name === "social" ? can("marketing.social")
      : r.name === "setup" ? can("branding.manage")
      : r.name === "data" ? can("data.export") || can("data.export.patient") || can("backup.manage")
      : true;
  const effectiveRoute: Route = route.name === "home" || !allowed(route) ? home : route;
  // Grouped by what people come to do: care, money, insight, running the clinic, and themselves.
  const navItems: { route: Route; label: string; icon: string; show: boolean; group: string; keywords?: string }[] = [
    { route: { name: "reception" }, label: t("reception"), icon: "reception", show: can("patient.search"), group: "Care", keywords: "patients register book appointment check in" },
    { route: { name: "worklist" }, label: t("worklist"), icon: "worklist", show: can("clinical.encounter.read"), group: "Care", keywords: "encounters notes today" },
    { route: { name: "care" }, label: "Care+", icon: "heart", show: can("care.desk") || can("care.manage") || can("care.tips.write"), group: "Care", keywords: "follow up reminders telegram sms health tips messages replies" },
    { route: { name: "signature" }, label: "My signature", icon: "signature", show: can("clinical.note.sign"), group: "Care" },
    { route: { name: "billing" }, label: "Billing", icon: "cash", show: can("billing.invoice.write"), group: "Money", keywords: "invoice payment receipt cashier drawer" },
    { route: { name: "finance" }, label: "Finance", icon: "ledger", show: can("finance.read"), group: "Money", keywords: "ledger close approvals prices trial balance" },
    { route: { name: "owner" }, label: "Owner reports", icon: "report", show: can("owner.reports.read"), group: "Money", keywords: "daily digest recipients" },
    { route: { name: "social" }, label: "Social media", icon: "megaphone", show: can("marketing.social"), group: "Insight", keywords: "facebook instagram tiktok telegram planner marketing followers" },
    { route: { name: "dashboard" }, label: "Dashboard", icon: "dashboard", show: can("analytics.read"), group: "Insight", keywords: "analytics charts insights attention" },
    { route: { name: "setup" }, label: "Clinic setup", icon: "building", show: can("branding.manage"), group: "Administration", keywords: "profile logo motto address phone licence tin stamp" },
    { route: { name: "team" }, label: "Team & roles", icon: "team", show: true, group: "Administration", keywords: "roles responsibilities permissions staff people" },
    { route: { name: "data" }, label: "Data & backups", icon: "database", show: can("data.export") || can("data.export.patient") || can("backup.manage"), group: "Administration", keywords: "export csv json backup restore verify download" },
    { route: { name: "settings" }, label: "Settings", icon: "settings", show: can("branding.manage") || can("printers.manage"), group: "Administration", keywords: "colour printers paper automation" },
    { route: { name: "admin" }, label: "System admin", icon: "shield", show: session.isPlatformAdmin, group: "Administration", keywords: "super admin organisations" },
    { route: { name: "account" }, label: "My account", icon: "key", show: true, group: "Me", keywords: "password" },
  ];
  const visibleNav = navItems.filter((n) => n.show);
  const navGroups = [...new Set(visibleNav.map((n) => n.group))];
  const commands: Command[] = [
    ...visibleNav.map((n) => ({ id: n.route.name, label: n.label, group: n.group, icon: n.icon, keywords: n.keywords, run: () => navigate(n.route) })),
    ...(can("backup.manage") ? [{ id: "backup-now", label: "Create an encrypted backup", group: "Action", icon: "database", keywords: "backup safe", run: () => navigatePath("/data") }] : []),
    ...(can("data.export") ? [{ id: "export-ledger", label: "Export the general journal", group: "Action", icon: "download", keywords: "ledger accountant csv", run: () => navigatePath("/data") }] : []),
    { id: "theme-dark", label: "Switch to dark theme", group: "Action", icon: "moon", keywords: "night", run: () => setTheme("dark") },
    { id: "theme-light", label: "Switch to light theme", group: "Action", icon: "sun", keywords: "day bright", run: () => setTheme("light") },
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
            {!facility?.logo && <Icon name="tooth" size={26} />}
            <span>{t("appName")}</span>
          </div>
          <button className="palette-trigger" onClick={() => setPaletteOpen(true)} aria-keyshortcuts="Control+K Meta+K">
            <Icon name="search" size={16} /> Jump to <kbd aria-hidden="true">Ctrl K</kbd>
          </button>
          {navGroups.map((g) => (
            <div key={g} className="nav-group">
              <h2 className="nav-group-title" id={`nav-${g}`}>{g}</h2>
              <ul aria-labelledby={`nav-${g}`}>
                {visibleNav.filter((n) => n.group === g).map((n) => (
                  <li key={n.route.name}>
                    <button aria-current={isCurrent(n.route) ? "page" : undefined} onClick={() => navigate(n.route)}>
                      <Icon name={n.icon} /> {n.label}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ))}
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
            {effectiveRoute.name === "dashboard" && <DashboardPage onNavigate={navigatePath} />}
            {effectiveRoute.name === "care" && <CarePage can={can} />}
            {effectiveRoute.name === "social" && <SocialPage />}
            {effectiveRoute.name === "setup" && <SetupPage onNavigate={navigatePath} onSettingsChanged={setFacility} />}
            {effectiveRoute.name === "team" && <TeamPage canSeeTeam={can("staff.admin")} canExport={can("data.export")} />}
            {effectiveRoute.name === "data" && <DataPage canExport={can("data.export") || can("data.export.patient")} canBackup={can("backup.manage")} canSeeHistory={can("audit.read")} />}
            {effectiveRoute.name === "settings" && <SettingsPage canBrand={can("branding.manage")} canPrinters={can("printers.manage")} onSettingsChanged={setFacility} />}
            {effectiveRoute.name === "signature" && <SignaturePage />}
            {effectiveRoute.name === "billing" && <BillingPage />}
            {effectiveRoute.name === "finance" && <FinancePage can={can} />}
            {effectiveRoute.name === "owner" && <OwnerPage canManage={can("owner.subscriptions.manage")} />}
            {effectiveRoute.name === "admin" && <AdminPage currentUserId={session.userId} />}
            {effectiveRoute.name === "account" && <AccountPage session={session} forced={false} onSession={onSession} />}
          </main>
        </div>
      </div>
      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} commands={commands} />}
    </>
  );
}
