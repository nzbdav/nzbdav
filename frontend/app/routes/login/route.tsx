import type { Route } from "./+types/route";
import { isAuthenticated, login } from "~/auth/authentication.server";
import { Form, redirect, useNavigation } from "react-router";
import { backendClient } from "~/clients/backend-client.server";
import { Alert, Button, Input, Spinner } from "~/components/ui";

type LoginPageData = {
    loginError: string
}

export async function loader({ request }: Route.LoaderArgs) {
    // if already logged in, redirect to landing page
    if (await isAuthenticated(request)) return redirect("/");

    // if we need to go through onboarding, redirect to onboarding page
    const isOnboarding = await backendClient.isOnboarding();
    if (isOnboarding) return redirect("/onboarding");

    // otherwise, proceed to login page!
    return { loginError: null };
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
    const navigation = useNavigation();
    const isLoading = navigation.state == "submitting";
    const pageData = actionData || loaderData;
    const showError = !!pageData.loginError;
    const submitButtonDisabled = isLoading;
    const submitButtonText = isLoading ? "Logging in..." : "Login";

    return (
        <main className="flex min-h-dvh w-full items-center justify-center bg-gray-900 px-4 py-8 text-white">
            <Form
                className="w-full max-w-sm space-y-5 rounded-xl border border-slate-700/70 bg-gray-800 p-6 shadow-xl shadow-black/20 sm:p-8"
                method="POST"
            >
                <div className="flex flex-col items-center gap-3 text-center">
                    <img className="h-16 w-16" src="/logo.svg" alt="NzbDav" />
                    <div>
                        <h1 className="text-2xl font-bold tracking-tight">NzbDav</h1>
                        <p className="mt-1 text-sm text-slate-400">Sign in to manage your server</p>
                    </div>
                </div>

                {showError && <Alert variant="danger">{pageData.loginError}</Alert>}

                <div className="space-y-4">
                    <label className="block space-y-1.5">
                        <span className="text-xs font-medium text-slate-300">Username</span>
                        <Input
                            className="w-full bg-slate-950/40 px-3 py-2.5"
                            name="username"
                            type="text"
                            placeholder="Enter your username"
                            autoComplete="username"
                            autoFocus
                        />
                    </label>
                    <label className="block space-y-1.5">
                        <span className="text-xs font-medium text-slate-300">Password</span>
                        <Input
                            className="w-full bg-slate-950/40 px-3 py-2.5"
                            name="password"
                            type="password"
                            placeholder="Enter your password"
                            autoComplete="current-password"
                        />
                    </label>
                </div>

                <Button
                    className="w-full"
                    type="submit"
                    size="medium"
                    variant="primary"
                    disabled={submitButtonDisabled}
                >
                    {isLoading && <Spinner className="text-white" />}
                    {submitButtonText}
                </Button>
            </Form>
        </main>
    );
}

export async function action({ request }: Route.ActionArgs) {
    try {
        const responseInit = await login(request);
        return redirect("/", responseInit);
    }
    catch (error) {
        if (error instanceof Error) {
            return { loginError: error.message };
        }
        throw error;
    }
}