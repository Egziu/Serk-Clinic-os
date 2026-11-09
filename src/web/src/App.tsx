import { useCallback, useEffect, useState } from "react";
import { api, ApiError, setCsrfToken } from "./api/client";
import type { Session } from "./api/types";
import { t } from "./i18n";
import { LoginPage } from "./pages/LoginPage";
import { ReceptionPage } from "./pages/ReceptionPage";
import { WorklistPage } from "./pages/WorklistPage";
import { EncounterPage } from "./pages/EncounterPage";

export type Route = { name: "home" } | { name: "reception" } | { name: "worklist" } | { name: "encounter"; id: string };

function parseRoute(): Route {
  const m = window.location.pathname.match(/^\/encounters\/([0-9a-f-]{36})$/i);
  if (m) return { name: "encounter", id: m[1]! };
  if (window.location.pathname.startsWith("/worklist")) return { name: "worklist" };
  if (window.location.pathname.startsWith("/reception")) return { name: "reception" };
  return { name: "home" };
}

function routeToPath(r: Route): string {
  return r.name === "encounter" ? `/encounters/${r.id}` : r.name === "home" ? "/" : `/${r.name}`;
}

export function App() {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const [route, setRoute] = useState<Route>(parseRoute);

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

  if (session === undefined) return <p className="sr-only" role="status">Loading…</p>;
  if (session === null || session.activeAffiliationId === null) {
    return <LoginPage session={session} onSession={onSession} />;
  }

  const can = (cap: string) => session.capabilities.includes(cap);
  const affiliation = session.affiliations.find((a) => a.id === session.activeAffiliationId);
  // Landing page follows the role's primary task: clinical staff -> worklist, front desk -> reception.
  const effectiveRoute: Route =
    route.name === "home" ? (can("clinical.encounter.read") ? { name: "worklist" } : { name: "reception" })
      : route.name === "reception" && !can("patient.search") ? { name: "worklist" } : route;

  const logout = async () => {
    try {
      await api.post("/api/v1/auth/logout");
    } finally {
      onSession(null);
      window.history.pushState(null, "", "/");
    }
  };

  return (
    <>
      <a href="#main" className="skip-link">Skip to main content</a>
      <div className="synthetic-banner" role="note">{t("syntheticBanner")}</div>
      <div className="shell">
        <nav className="shell-nav" aria-label="Primary">
          <div className="brand">{t("appName")}</div>
          <ul>
            {can("patient.search") && (
              <li>
                <button aria-current={effectiveRoute.name === "reception" ? "page" : undefined} onClick={() => navigate({ name: "reception" })}>
                  {t("reception")}
                </button>
              </li>
            )}
            {can("clinical.encounter.read") && (
              <li>
                <button aria-current={effectiveRoute.name === "worklist" || effectiveRoute.name === "encounter" ? "page" : undefined} onClick={() => navigate({ name: "worklist" })}>
                  {t("worklist")}
                </button>
              </li>
            )}
          </ul>
        </nav>
        <div className="shell-main">
          <header className="topbar">
            <div className="context">
              <strong>{affiliation?.facilityName}</strong> · {affiliation?.tenantName}
            </div>
            <div className="btn-row">
              <span className="context">
                Signed in as <strong>{session.displayName}</strong> ({affiliation?.roles.join(", ")})
              </span>
              <button className="btn" onClick={logout}>{t("signOut")}</button>
            </div>
          </header>
          <main id="main" tabIndex={-1}>
            {effectiveRoute.name === "reception" && can("patient.search") && <ReceptionPage session={session} />}
            {effectiveRoute.name === "worklist" && <WorklistPage onOpen={(id) => navigate({ name: "encounter", id })} />}
            {effectiveRoute.name === "encounter" && <EncounterPage id={effectiveRoute.id} session={session} onBack={() => navigate({ name: "worklist" })} />}
            {effectiveRoute.name === "reception" && !can("patient.search") && !can("clinical.encounter.read") && (
              <div className="alert alert-info">Your role has no clinical or front-desk workspace in this version.</div>
            )}
          </main>
        </div>
      </div>
    </>
  );
}
