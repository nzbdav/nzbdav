import type { Route } from "./+types/route";
import { useState } from "react";
import { backendClient } from "~/clients/backend-client.server";
import { Form, redirect, useNavigation } from "react-router";
import { isAuthenticated, setSessionUser } from "~/auth/authentication.server";
import { Alert, Button, Input, Spinner } from "~/components/ui";

type OnboardingPageData = {
    error: string
}

export async function loader({ request }: Route.LoaderArgs) {
    // if already logged in, redirect to landing page
    if (await isAuthenticated(request)) return redirect("/")

    // if we don't need to go through onboarding, redirect to login page
    const isOnboarding = await backendClient.isOnboarding();
    if (!isOnboarding) return redirect("/login");

    // otherwise, proceed to onboarding page!
    return { error: null };
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
    const pageData = actionData || loaderData;
    const [username, setUsername] = useState("");
    const [password, setPassword] = useState("");
    const [confirmPassword, setConfirmPassword] = useState("");

    const navigation = useNavigation();
    const isLoading = navigation.state == "submitting";

    let submitButtonDisabled = false;
    let submitButtonText = "Register";
    if (isLoading) {
        submitButtonDisabled = true;
        submitButtonText = "Registering...";
    } else if (username == "") {
        submitButtonDisabled = true;
        submitButtonText = "Username is required";
    } else if (password === "") {
        submitButtonDisabled = true;
        submitButtonText = "Password is required";
    } else if (password != confirmPassword) {
        submitButtonDisabled = true;
        submitButtonText = "Passwords must match";
    }

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
                        <p className="mt-1 text-sm text-slate-400">Set up your administrator account</p>
                    </div>
                </div>

                {pageData.error &&
                    <Alert variant="danger">
                        {pageData.error}
                    </Alert>
                }
                {!pageData.error &&
                    <Alert variant="warning">
                        <p className="mb-1 font-semibold">Welcome!</p>
                        Create credentials for managing your NzbDav server.
                    </Alert>
                }

                <div className="space-y-4">
                    <label className="block space-y-1.5">
                        <span className="text-xs font-medium text-slate-300">Username</span>
                        <Input
                            className="w-full bg-slate-950/40 px-3 py-2.5"
                            autoFocus
                            name="username"
                            type="text"
                            placeholder="Choose a username"
                            autoComplete="username"
                            value={username}
                            onChange={e => setUsername(e.currentTarget.value)}
                        />
                    </label>
                    <label className="block space-y-1.5">
                        <span className="text-xs font-medium text-slate-300">Password</span>
                        <Input
                            className="w-full bg-slate-950/40 px-3 py-2.5"
                            name="password"
                            type="password"
                            placeholder="Choose a password"
                            autoComplete="new-password"
                            value={password}
                            onChange={e => setPassword(e.currentTarget.value)}
                        />
                    </label>
                    <label className="block space-y-1.5">
                        <span className="text-xs font-medium text-slate-300">Confirm password</span>
                        <Input
                            className="w-full bg-slate-950/40 px-3 py-2.5"
                            type="password"
                            placeholder="Repeat your password"
                            autoComplete="new-password"
                            value={confirmPassword}
                            onChange={e => setConfirmPassword(e.currentTarget.value)}
                        />
                    </label>
                </div>

                <Button
                    className="w-full"
                    type="submit"
                    size="medium"
                    variant="primary"
                    disabled={submitButtonDisabled}>
                    {isLoading && <Spinner className="text-white" />}
                    {submitButtonText}
                </Button>
                <p className="text-center text-xs text-slate-500">
                    First-time setup · this account becomes the administrator
                </p>
            </Form>
        </main>
    );
}

export async function action({ request }: Route.ActionArgs) {
    try {
        // if already logged in, redirect to landing page
        if (await isAuthenticated(request)) return redirect("/")

        // if we don't need to go through onboarding, redirect to login page
        const isOnboarding = await backendClient.isOnboarding();
        if (!isOnboarding) return redirect("/login");

        // finish onboarding
        const formData = await request.formData();
        const username = formData.get("username")?.toString();
        const password = formData.get("password")?.toString();
        if (!username || !password) throw new Error("username and password required");
        const isSuccess = await backendClient.createAccount(username, password);
        if (!isSuccess) throw new Error("Unknown error creating account");
        const responseInit = await setSessionUser(request, username);
        return redirect("/", responseInit);
    }
    catch (error) {
        if (error instanceof Error) {
            return { error: error.message };
        }
        throw error
    }
}