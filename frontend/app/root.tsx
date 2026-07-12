import {
  isRouteErrorResponse,
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
  useRouteError,
} from "react-router";

import "./app.css";
import type { Route } from "./+types/root";
import { IS_FRONTEND_AUTH_DISABLED } from "~/auth/authentication.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";
import { getAppVersion } from "./utils/version.server";
import { backendClient } from "./clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
  // Single-fetch navigation/revalidation uses internal `.data` URLs
  // (e.g. /login.data), so strip that suffix before the layout check.
  let path = new URL(request.url).pathname.replace(/\.data$/, "");
  if (path === "/login" || path === "/onboarding") {
    return { useLayout: false };
  }

  const config = await backendClient.getConfig([
    "usenet.providers",
    "play.watchdog-enabled",
  ]);

  return {
    useLayout: true,
    version: await getAppVersion(),
    isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
    hasUsenetProviders: hasConfiguredUsenetProviders(
      config.find(item => item.configName === "usenet.providers")?.configValue
    ),
    isWatchdogEnabled:
      config.find(item => item.configName === "play.watchdog-enabled")?.configValue?.toLowerCase() !== "false",
  };
}

function hasConfiguredUsenetProviders(configValue?: string): boolean {
  if (!configValue) return false;

  try {
    const config = JSON.parse(configValue);
    return Array.isArray(config?.Providers) && config.Providers.length > 0;
  } catch {
    return false;
  }
}


export function Layout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-appearance-theme="dark">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/logo.svg" />
        <link rel="preconnect" href="https://fonts.googleapis.com" />
        <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="anonymous" />
        <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const {
    useLayout,
    version,
    isFrontendAuthDisabled,
    hasUsenetProviders,
    isWatchdogEnabled,
  } = loaderData;
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);
  const hideShell =
    location.pathname === "/login" || location.pathname === "/onboarding";

  if (useLayout && !hideShell) {
    return (
      <PageLayout
        topNavComponent={TopNavigation}
        bodyChild={showLoading ? <Loading /> : <Outlet />}
        leftNavChild={
          <LeftNavigation
            version={version}
            isFrontendAuthDisabled={isFrontendAuthDisabled}
            hasUsenetProviders={hasUsenetProviders}
            isWatchdogEnabled={isWatchdogEnabled} />
        } />
    );
  }

  return <Outlet />;
}

// Root ErrorBoundary catches loader/component throws that aren't handled closer
// to the route. Without this, an SSR loader that rejects (e.g. backend fetch
// timeout while the backend is busy) bubbles to React Router's default 500 with
// no UI. Keep this page free of PageLayout so we don't re-run the root loader
// and loop back into the same failure. Adopted from elfhosted/rebased-v3.
export function ErrorBoundary() {
  const error = useRouteError();
  const isUndiciTimeout =
    error instanceof Error &&
    /fetch failed|ConnectTimeoutError|HeadersTimeoutError|UND_ERR_CONNECT_TIMEOUT|UND_ERR_HEADERS_TIMEOUT/i.test(
      `${error.message} ${(error.cause as Error)?.message ?? ""}`,
    );

  let title = "Something went wrong";
  let detail: string;
  if (isUndiciTimeout) {
    title = "Backend temporarily unavailable";
    detail =
      "The nzbdav backend is still starting up or is busy processing a large queue. Wait a moment and refresh the page.";
  } else if (isRouteErrorResponse(error)) {
    title = `${error.status} ${error.statusText}`;
    detail = typeof error.data === "string" ? error.data : "";
  } else if (error instanceof Error) {
    detail = error.message;
  } else {
    detail = "Unknown error.";
  }

  return (
    <main className="flex min-h-dvh w-full items-center justify-center bg-gray-900 px-4 py-8 text-white">
      <div className="w-full max-w-lg space-y-4 rounded-xl border border-slate-700/70 bg-gray-800 p-6 shadow-xl shadow-black/20 sm:p-8">
        <div className="space-y-2">
          <h1 className="text-2xl font-bold tracking-tight">{title}</h1>
          {detail ? <p className="text-sm leading-relaxed text-slate-300">{detail}</p> : null}
        </div>
        <button
          type="button"
          className="button-small flex items-center justify-center gap-2 bg-blue-500 hover:bg-blue-600"
          onClick={() => window.location.reload()}
        >
          Reload
        </button>
      </div>
    </main>
  );
}